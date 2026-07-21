using System;
using Microsoft.Extensions.DependencyInjection;
using RASTA.App.ViewModels;

namespace RASTA.App.Services
{
    public class NavigationService
    {
        private readonly IServiceProvider _services;

        // The currently active ViewModel
        public object? CurrentViewModel { get; private set; }

        public NavigationService(IServiceProvider services)
        {
            _services = services;
        }

        // Generic navigation method
        public void NavigateTo<TViewModel>() where TViewModel : class
        {
            CurrentViewModel = _services.GetRequiredService<TViewModel>();
        }

        // Strongly-typed convenience methods
        public void NavigateToPrepare() =>
            NavigateTo<PrepareViewModel>();

        public void NavigateToPlan() =>
            NavigateTo<PlanViewModel>();

        public void NavigateToObserve() =>
            NavigateTo<ObserveViewModel>();

        public void NavigateToProcess() =>
            NavigateTo<ProcessViewModel>();

        public void NavigateToVisualise() =>
            NavigateTo<VisualiseViewModel>();
    }
}
