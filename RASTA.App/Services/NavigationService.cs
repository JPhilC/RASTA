using System;
using Microsoft.Extensions.DependencyInjection;
using RASTA.App.ViewModels;

namespace RASTA.App.Services
{
    public class NavigationService
    {
        private readonly IServiceProvider _services;
        private readonly NavigationViewModel _navigation;

        public NavigationService(IServiceProvider services)
        {
            _services = services;
            _navigation = _services.GetRequiredService<NavigationViewModel>();
        }

        public void NavigateTo<TViewModel>() where TViewModel : class
        {
            var vm = _services.GetRequiredService<TViewModel>();
            _navigation.Navigate(vm);
        }

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
