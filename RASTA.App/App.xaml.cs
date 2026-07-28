using Microsoft.Extensions.DependencyInjection;
using RASTA.App.Services;
using RASTA.App.ViewModels;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Fft;
using RASTA.Infrastructure.Logging;
using RASTA.Infrastructure.Sdr;
using RASTA.Infrastructure.Storage;
using RASTA.Infrastructure.Telescope;
using RASTA.Processing.Calibration;
using RASTA.Processing.Capture;
using RASTA.Processing.Gridding;
using RASTA.Processing.Planning;
using RASTA.Processing.Spectral;
using RASTA.Processing.VisualisationData;
using System;
using System.Windows;

namespace RASTA.App
{
    public partial class App : Application
    {
        public static IServiceProvider Services { get; private set; } = default!;

        protected override void OnStartup(StartupEventArgs e)
        {
            var services = new ServiceCollection();

            // ---------------------------------------------------------
            // Infrastructure
            // ---------------------------------------------------------
            services.AddSingleton(new RastaLogger("Logs/rasta.log"));

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

            // USB watcher (attaches to window handle when resolved)
            services.AddSingleton<UsbWatcherService>();

            // ---------------------------------------------------------
            // FFT engine
            // ---------------------------------------------------------
            services.AddSingleton<IFftEngine, FftEngine>();

            // ---------------------------------------------------------
            // Calibration + Observation
            // ---------------------------------------------------------
            services.AddSingleton<Calibrator>();
            services.AddSingleton<ObservationCaptureService>();

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
            services.AddSingleton<FitsFileIo>();
            services.AddSingleton<SdrRawCaptureService>();

            // ---------------------------------------------------------
            // Processing
            // ---------------------------------------------------------
            services.AddSingleton<SpectrumMath>();
            services.AddSingleton<SpectrumImageBuilder>();
            services.AddSingleton<HeatmapBuilder>();
            services.AddSingleton<GridBuilder>();

            // ---------------------------------------------------------
            // ViewModels (must be transient)
            // ---------------------------------------------------------
            services.AddTransient<StatusBarViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<PrepareViewModel>();
            services.AddTransient<PlanViewModel>();
            services.AddTransient<ObserveViewModel>();
            services.AddTransient<ProcessViewModel>();
            services.AddTransient<VisualiseViewModel>();
            services.AddTransient<NavigationService>();
            services.AddTransient<NavigationViewModel>();

            // ---------------------------------------------------------
            // Build provider
            // ---------------------------------------------------------
            Services = services.BuildServiceProvider();

            base.OnStartup(e);
        }


    }
}
