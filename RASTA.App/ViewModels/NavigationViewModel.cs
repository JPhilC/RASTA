using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;

namespace RASTA.App.ViewModels
{
    public partial class NavigationViewModel : ObservableObject
    {
        private readonly NavigationService _nav;

        public SettingsViewModel Settings { get; }

        public StatusBarViewModel StatusBarViewModel { get; }

        [ObservableProperty]
        private object? currentViewModel;

        public NavigationViewModel(NavigationService nav, SettingsViewModel settings, StatusBarViewModel statusBarViewModel)
        {
            _nav = nav;
            Settings = settings;
            StatusBarViewModel = statusBarViewModel;
        }

        private void UpdateView() =>
            CurrentViewModel = _nav.CurrentViewModel;

        [RelayCommand]
        private void NavigatePrepare()
        {
            _nav.NavigateTo<PrepareViewModel>();
            UpdateView();
        }

        [RelayCommand]
        private void NavigatePlan()
        {
            _nav.NavigateTo<PlanViewModel>();
            UpdateView();
        }

        [RelayCommand]
        private void NavigateObserve()
        {
            _nav.NavigateTo<ObserveViewModel>();
            UpdateView();
        }

        [RelayCommand]
        private void NavigateProcess()
        {
            _nav.NavigateTo<ProcessViewModel>();
            UpdateView();
        }

        [RelayCommand]
        private void NavigateVisualise()
        {
            _nav.NavigateTo<VisualiseViewModel>();
            UpdateView();
        }
    }
}
