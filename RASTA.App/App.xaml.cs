using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;
using RASTA.App.ViewModels;
using RASTA.Infrastructure.Logging;
using RASTA.Processing.Spectral;
using RASTA.Processing.VisualisationData;
using RASTA.Processing.Gridding;
using RASTA.Processing.Planning;
using RASTA.Processing.Capture;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Telescope;
using RASTA.App.Services;

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

            // Telescope + SDR (you will replace these with real implementations)
            services.AddSingleton<ITelescopeMount>(provider =>
            {
                // Alpaca Remote default port is 11111
                // Telescope device 0 is the first telescope
                string alpacaBaseUrl = "http://localhost:11111/api/v1/telescope/0";

                return new AscomTelescopeMount(alpacaBaseUrl);
            });
            
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
            services.AddSingleton<PrepareViewModel>();
            services.AddSingleton<PlanViewModel>();
            services.AddSingleton<ObserveViewModel>();
            services.AddSingleton<ProcessViewModel>();
            services.AddSingleton<VisualiseViewModel>();
            services.AddSingleton<NavigationViewModel>();
            services.AddSingleton<NavigationService>();

            Services = services.BuildServiceProvider();

            base.OnStartup(e);
        }
    }
}
