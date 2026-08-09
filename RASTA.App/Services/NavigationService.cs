using System;
using Microsoft.Extensions.DependencyInjection;
using RASTA.App.ViewModels;

namespace RASTA.App.Services
{
    public class NavigationService
    {
        private readonly IServiceProvider _services;

        public object? CurrentViewModel { get; private set; }

        public NavigationService(IServiceProvider services)
        {
            _services = services;
        }

        // Basic navigation
        public void NavigateTo<TViewModel>() where TViewModel : class
        {
            CurrentViewModel = _services.GetRequiredService<TViewModel>();
        }

        // Navigation with configuration
        public void NavigateTo<TViewModel>(Action<TViewModel> configure)
            where TViewModel : class
        {
            var vm = _services.GetRequiredService<TViewModel>();
            configure(vm);
            CurrentViewModel = vm;
        }

        // Convenience methods
        public void NavigateToPrepare() =>
            NavigateTo<PrepareViewModel>();

        public void NavigateToPlan() =>
            NavigateTo<PlanViewModel>();

        public void NavigateToCapture() =>
            NavigateTo<CaptureViewModel>();

        public void NavigateToVisualise() =>
            NavigateTo<VisualiseViewModel>();
    }
}
