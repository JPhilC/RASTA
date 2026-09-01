using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Helpers;
using RASTA.App.Services;

namespace RASTA.App.ViewModels
{

    public partial class NavigationViewModel : ObservableObject
    {

        public enum NavigationSection
        {
            Prepare,
            Plan,
            Capture,
            Visualise,
            Options
        }

        [ObservableProperty]
        private NavigationSection currentSection;

        private readonly NavigationService _nav;

        private readonly CalibrationService _calibrationService;

        public SettingsViewModel Settings { get; }

        public StatusBarViewModel StatusBarViewModel { get; }

        // Plan needs neither a mount nor an SDR - PlanType/CoordinateMode is a free choice on
        // the Plan screen itself, plans are no longer tied to a specific SDR device, and the
        // sky map (background/points/region drawing) works from site settings alone, so
        // planning can be done fully offline. The one thing that does need a connected mount +
        // SDR is the map's right-click "Slew & Capture Here", gated separately by
        // PlanViewModel.CanCaptureHere.

        // Capture drives an actual mount slew (and CaptureViewModel.LoadAvailablePlans
        // filters plans by the mount's detected CoordinateMode), so it needs both an SDR
        // and a connected mount, not just the SDR.
        public bool CanNavigateCapture => StatusBarViewModel.SdrConnected && StatusBarViewModel.TelescopeConnected;

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
                if (args.PropertyName == nameof(StatusBarViewModel.SdrConnected) ||
                    args.PropertyName == nameof(StatusBarViewModel.TelescopeConnected))
                {
                    OnPropertyChanged(nameof(CanNavigateCapture));
                    // Update command state on UI thread
                    UiThread.SafeInvoke(() => NavigateCaptureCommand.NotifyCanExecuteChanged());
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

        [RelayCommand]
        private void NavigatePlan()
        {
            CurrentSection = NavigationSection.Plan;

            _nav.NavigateTo<PlanViewModel>();
            UpdateView();
        }

        [RelayCommand(CanExecute = nameof(CanNavigateCapture))]
        private void NavigateCapture()
        {
            CurrentSection = NavigationSection.Capture;

            // Refresh the plan dropdown each time Capture is opened, in case plans were
            // added/edited/deleted on the Plan screen since CaptureViewModel was created
            // (it's effectively a singleton for the app's lifetime - see CLAUDE.md).
            // Selection itself now lives entirely in CaptureViewModel, not pushed in from
            // PlanViewModel.SelectedPlan.
            _nav.NavigateTo<CaptureViewModel>(vm =>
            {
                vm.LoadAvailablePlans();
            });

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
