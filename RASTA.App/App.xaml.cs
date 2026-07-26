using Microsoft.Extensions.DependencyInjection;
using RASTA.App.Services;
using RASTA.App.ViewModels;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Logging;
using RASTA.Infrastructure.Sdr;
using RASTA.Infrastructure.Telescope;
using RASTA.Infrastructure.Storage;
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
            // Shared telescope state (required!)
            // ---------------------------------------------------------
            services.AddSingleton<TelescopeState>();

            // ---------------------------------------------------------
            // Shared SDR state (required!)
            // ---------------------------------------------------------
            services.AddSingleton<SdrState>();

            // ---------------------------------------------------------
            // SDR services
            // ---------------------------------------------------------
            services.AddSingleton<SdrDeviceService>();
            services.AddSingleton<UsbWatcherService>();   // auto-starts when resolved

            // ---------------------------------------------------------
            // Alpaca Client (session-wide)
            // ---------------------------------------------------------
            services.AddSingleton<AscomAlpacaClient>();

            // ---------------------------------------------------------
            // Telescope Mount (session-wide)
            // ---------------------------------------------------------
            services.AddSingleton<ITelescopeMount>(provider =>
            {
                var alpaca = provider.GetRequiredService<AscomAlpacaClient>();
                return new AscomTelescopeMount(alpaca);
            });

            // ---------------------------------------------------------
            // Planning service (session-wide)
            // ---------------------------------------------------------
            services.AddSingleton<SweepPlanner>();
            services.AddSingleton<IPlanRepository, JsonPlanRepository>();

            // ---------------------------------------------------------
            // Telescope telemetry service (session-wide)
            // ---------------------------------------------------------
            services.AddSingleton<TelescopeService>();

            // ---------------------------------------------------------
            // Radio capture pipeline (session-wide)
            // ---------------------------------------------------------
            services.AddSingleton<ISdrDevice, RtlSdrDevice>();
            services.AddSingleton<FitsFileIo>();
            services.AddSingleton<SdrRawCaptureService>();
            services.AddSingleton<ObservationCaptureService>();

            // ---------------------------------------------------------
            // Processing
            // ---------------------------------------------------------
            services.AddSingleton<SpectrumMath>();
            services.AddSingleton<SpectrumImageBuilder>();
            services.AddSingleton<HeatmapBuilder>();
            services.AddSingleton<GridBuilder>();
            services.AddSingleton<SweepPlanner>();

            // ---------------------------------------------------------
            // ViewModels
            // ---------------------------------------------------------
            services.AddSingleton<StatusBarViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<PrepareViewModel>();
            services.AddSingleton<PlanViewModel>();
            services.AddSingleton<ObserveViewModel>();
            services.AddSingleton<ProcessViewModel>();
            services.AddSingleton<VisualiseViewModel>();
            services.AddSingleton<NavigationService>();
            services.AddSingleton<NavigationViewModel>();

            // ---------------------------------------------------------
            // Build provider
            // ---------------------------------------------------------
            Services = services.BuildServiceProvider();

            base.OnStartup(e);
        }

    }
}
