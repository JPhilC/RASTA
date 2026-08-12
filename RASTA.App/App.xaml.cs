using Microsoft.Extensions.DependencyInjection;
using RASTA.App.Helpers;
using RASTA.App.Services;
using RASTA.App.ViewModels;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Fft;
using RASTA.Infrastructure.Logging;
using RASTA.Infrastructure.Services;
using RASTA.Infrastructure.Storage;
using RASTA.Infrastructure.Telescope;
using RASTA.Processing.Calibration;
using RASTA.Processing.Gridding;
using RASTA.Processing.Mosaic;
using RASTA.Processing.Planning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RASTA.App
{
    public partial class App : Application
    {
        public static IServiceProvider Services { get; private set; } = default!;

        private RastaLogger? _logger;

        protected override void OnStartup(StartupEventArgs e)
        {
            // ---------------------------------------------------------
            // Global exception handling. Without these, ANY unhandled
            // exception - on the UI thread, on a background thread (e.g.
            // TelescopeService's polling loop or a Timer callback), or from
            // an unobserved fire-and-forget Task - takes the whole process
            // down with no log entry and no message to the user. Hooked up
            // before anything else runs, since a startup-time failure is
            // exactly the kind of thing this is meant to catch too.
            // ---------------------------------------------------------
            // Per-user LocalAppData, not a relative path - matches UserOptionsService's convention.
            // A relative path resolves against the process's working directory, which for an
            // installed Start Menu shortcut is Program Files - not writable by a standard user.
            // Directory.CreateDirectory there throws before the handlers below are even wired up,
            // so the app dies silently on startup with no window and no message box.
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RASTA", "Logs", "rasta.log");
            _logger = new RastaLogger(logPath);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            var services = new ServiceCollection();

            // ---------------------------------------------------------
            // Infrastructure
            // ---------------------------------------------------------
            services.AddSingleton(_logger);
            services.AddSingleton<UserOptionsService>();
            // ---------------------------------------------------------
            // Telescope state
            // ---------------------------------------------------------
            services.AddSingleton<TelescopeState>();

            // ---------------------------------------------------------
            // SDR state + services
            // ---------------------------------------------------------
            services.AddSingleton<SdrState>();
            services.AddSingleton<SdrDeviceService>();   // owns persistent device

            // Lazy SDR device proxy — may be null until watcher enumerates
            services.AddSingleton<ISdrDevice>(provider =>
            {
                var svc = provider.GetRequiredService<SdrDeviceService>();
                return svc.GetDevice();   // DO NOT throw here
            });

            // ---------------------------------------------------------
            // USB watcher (attaches to window handle when resolved)
            // ---------------------------------------------------------
            services.AddSingleton<UsbWatcherService>();


            services.AddSingleton<IUserPromptService, MessageBoxPromptService>();

            // ---------------------------------------------------------
            // FFT engine
            // ---------------------------------------------------------
            services.AddTransient<IFftEngine, FftEngine>();

            // ---------------------------------------------------------
            // Calibration + Observation
            // ---------------------------------------------------------
            services.AddSingleton<CalibrationRepository>();
            services.AddSingleton<CalibrationService>();
            services.AddSingleton<Calibrator>();

            // ---------------------------------------------------------
            // Alpaca client
            // ---------------------------------------------------------
            services.AddSingleton<AscomAlpacaClient>();

            // ---------------------------------------------------------
            // Telescope mount
            // ---------------------------------------------------------
            services.AddSingleton<ITelescopeMount>(provider =>
            {
                var alpaca = provider.GetRequiredService<AscomAlpacaClient>();
                return new AscomTelescopeMount(alpaca);
            });

            // ---------------------------------------------------------
            // Planning
            // ---------------------------------------------------------
            services.AddSingleton<SweepPlanner>();
            services.AddSingleton<IPlanRepository, JsonPlanRepository>();

            // ---------------------------------------------------------
            // Telescope telemetry
            // ---------------------------------------------------------
            services.AddSingleton<TelescopeService>();

            // ---------------------------------------------------------
            // Radio capture pipeline
            // ---------------------------------------------------------
            services.AddTransient<FitsFileIo>();

            // ---------------------------------------------------------
            // Processing
            // ---------------------------------------------------------
            services.AddSingleton<GridBuilder>();
            services.AddSingleton<MosaicProcessor>();

            // ---------------------------------------------------------
            // ViewModels
            // ---------------------------------------------------------
            services.AddSingleton<StatusBarViewModel>();
            services.AddScoped<SettingsViewModel>();
            services.AddScoped<PrepareViewModel>();
            services.AddScoped<PlanViewModel>();
            services.AddScoped<CaptureViewModel>();
            services.AddScoped<MosaicViewModel>();
            services.AddScoped<VisualiseViewModel>();
            services.AddScoped<UserOptionsViewModel>();
            services.AddSingleton<NavigationService>();
            services.AddSingleton<NavigationViewModel>();

            // ---------------------------------------------------------
            // Build provider
            // ---------------------------------------------------------
            Services = services.BuildServiceProvider();

            // See OnTelescopeConnectionLost below - the mount side's equivalent of
            // SdrDeviceService's hot-plug handling.
            Services.GetRequiredService<TelescopeService>().ConnectionLost += OnTelescopeConnectionLost;

            base.OnStartup(e);
        }

        /// <summary>
        /// Stops the app's background loops/watchers - TelescopeService's mount-polling
        /// Task.Run loop, UsbWatcherService's USB debounce Timer, SdrDeviceService's device
        /// handle - before shutdown finishes. Without this, they keep running past window
        /// close and can still flip SdrState/TelescopeState.IsConnected from a background
        /// thread; that PropertyChanged bubbles up into NavigationViewModel/PrepareViewModel,
        /// which try to marshal onto Application.Current.Dispatcher - but Application.Current
        /// can already be null by then, throwing a NullReferenceException (this is what
        /// UiThread.SafeInvoke guards against too, but stopping the source here is the real
        /// fix rather than just tolerating it at every call site).
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Services.GetService<TelescopeService>()?.Stop();
                Services.GetService<UsbWatcherService>()?.Dispose();
                Services.GetService<SdrDeviceService>()?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Error($"Error during shutdown cleanup: {ex.Message}");
            }

            base.OnExit(e);
        }

        /// <summary>
        /// Fires when TelescopeService's poll loop learns the mount is unreachable (a live
        /// ASCOM Alpaca call throwing - network drop, mount powered off, Alpaca server gone).
        /// This is the mount side's equivalent of SdrDeviceService's hot-plug handling, but
        /// unlike the SDR side there's no automatic reconnect attempt: once a live poll has
        /// failed there's no way to know what physical state the mount was actually left in
        /// (mid-slew? tracking? parked?), so reconnecting is left as a deliberate, informed
        /// action for the user rather than something done silently on their behalf.
        /// Fires on TelescopeService's own background polling thread, so everything here is
        /// marshaled onto the UI thread via UiThread.SafeInvoke. Tidies up exactly as if the
        /// user had clicked Disconnect: cancels any capture that might be running (so its
        /// in-flight FITS file is never written - see CaptureViewModel.CancelAnyRunningCapture),
        /// resets connection state without any further live mount I/O (the link is already
        /// known down - see SettingsViewModel.ForceDisconnectTelescope), and returns to
        /// Prepare, since Plan/Capture both require a connected mount to mean anything. The
        /// MessageBox goes up last, after the view has already switched back to Prepare, so
        /// it doesn't sit in front of whatever view the user was actually on when the mount
        /// dropped - dismissing it lands them straight on the Prepare screen it's explaining.
        /// </summary>
        private void OnTelescopeConnectionLost(Exception ex)
        {
            _logger?.Warn($"Telescope connection lost: {ex.Message}");

            UiThread.SafeInvoke(() =>
            {
                Services.GetRequiredService<CaptureViewModel>().CancelAnyRunningCapture();
                Services.GetRequiredService<SettingsViewModel>().ForceDisconnectTelescope();
                Services.GetRequiredService<TelescopeService>().Stop();
                Services.GetRequiredService<NavigationViewModel>().NavigatePrepareCommand.Execute(null);

                MessageBox.Show(
                    $"The connection to the telescope mount was lost:\n\n{ex.Message}\n\n" +
                    "Any running capture has been cancelled and the mount has been disconnected. " +
                    "Please check the mount/Alpaca connection and reconnect from here when ready.",
                    "Telescope Connection Lost",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        }

        // ---------------------------------------------------------
        // Global exception handlers
        // ---------------------------------------------------------

        /// <summary>
        /// Catches anything unhandled that reaches the WPF dispatcher (UI thread) - e.g. an
        /// exception escaping a XAML event handler, or a RelayCommand path that isn't fully
        /// wrapped in its own try/catch. Logs it and shows the user a message instead of
        /// silently crashing; e.Handled = true lets the app keep running.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger?.Error($"Unhandled UI exception: {e.Exception}");

            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n" +
                "The application will try to continue - you may want to save your work and restart.",
                "Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }

        /// <summary>
        /// Catches unhandled exceptions on any other thread (e.g. a raw ThreadPool/Timer
        /// callback such as UsbWatcherService's debounce timer, or TelescopeService's
        /// background polling loop if it were ever changed to rethrow). Unlike the
        /// dispatcher handler above, this cannot stop the process from terminating
        /// (e.IsTerminating is almost always true by the time this fires) - the best that
        /// can be done is make sure it's logged and, best-effort, surfaced to the user
        /// before the app goes down, instead of it happening silently.
        /// </summary>
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            _logger?.Error($"Fatal unhandled exception (process is terminating): {e.ExceptionObject}");

            try
            {
                MessageBox.Show(
                    $"A fatal error occurred and R.A.S.T.A. must close:\n\n{(e.ExceptionObject as Exception)?.Message}",
                    "Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Best effort only - the process may already be too far gone to show UI.
            }
        }

        /// <summary>
        /// Catches exceptions from a Task whose fault was never observed (awaited or
        /// otherwise inspected) - e.g. a fire-and-forget Task.Run. Logging + SetObserved()
        /// here means these get recorded instead of vanishing silently.
        /// </summary>
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            _logger?.Error($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        }
    }
}
