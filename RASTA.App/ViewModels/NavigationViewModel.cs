using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using System.Windows;

namespace RASTA.App.ViewModels
{

    public partial class NavigationViewModel : ObservableObject
    {

        public enum NavigationSection
        {
            Prepare,
            Plan,
            Observe,
            Process,
            Visualise,
            Options
        }

        [ObservableProperty]
        private NavigationSection currentSection;

        private readonly NavigationService _nav;

        private readonly CalibrationService _calibrationService;

        public SettingsViewModel Settings { get; }

        public StatusBarViewModel StatusBarViewModel { get; }

        public bool CanObserve => StatusBarViewModel.SdrConnected;

        [ObservableProperty]
        private object? currentViewModel;

        public NavigationViewModel(NavigationService nav, SettingsViewModel settings, StatusBarViewModel statusBarViewModel, CalibrationService calibrationService)
        {
            _nav = nav;
            Settings = settings;
            StatusBarViewModel = statusBarViewModel;
            _calibrationService = calibrationService;

            StatusBarViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(StatusBarViewModel.SdrConnected))
                {
                    OnPropertyChanged(nameof(CanObserve));
                    // Update command state on UI thread
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        NavigatePlanCommand.NotifyCanExecuteChanged();
                        NavigateObserveCommand.NotifyCanExecuteChanged();
                    });
                }
            };
        }

        private void UpdateView() =>
            CurrentViewModel = _nav.CurrentViewModel;

        [RelayCommand]
        private void NavigatePrepare()
        {
            CurrentSection = NavigationSection.Prepare;

            _nav.NavigateTo<PrepareViewModel>();
            UpdateView();
        }

        [RelayCommand(CanExecute = nameof(CanObserve))]
        private void NavigatePlan()
        {
            CurrentSection = NavigationSection.Plan;

            _nav.NavigateTo<PlanViewModel>();
            UpdateView();
        }

        [RelayCommand(CanExecute = nameof(CanObserve))]
        private void NavigateObserve()
        {
            CurrentSection = NavigationSection.Observe;

            // Refresh the plan dropdown each time Observe is opened, in case plans were
            // added/edited/deleted on the Plan screen since ObserveViewModel was created
            // (it's effectively a singleton for the app's lifetime - see CLAUDE.md).
            // Selection itself now lives entirely in ObserveViewModel, not pushed in from
            // PlanViewModel.SelectedPlan.
            _nav.NavigateTo<ObserveViewModel>(vm =>
            {
                vm.LoadAvailablePlans();
            });

            UpdateView();
        }

        [RelayCommand]
        private void NavigateProcess()
        {
            CurrentSection = NavigationSection.Process;

            _nav.NavigateTo<ProcessViewModel>();
            UpdateView();
        }

        [RelayCommand]
        private void NavigateVisualise()
        {
            CurrentSection = NavigationSection.Visualise;

            _nav.NavigateTo<VisualiseViewModel>();
            UpdateView();
        }

        [RelayCommand]
        private void NavigateUserOptions()
        {
            CurrentSection = NavigationSection.Options;

            _nav.NavigateTo<UserOptionsViewModel>();
            UpdateView();
        }

    }
}
