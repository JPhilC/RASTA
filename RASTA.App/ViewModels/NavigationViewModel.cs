using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;

public partial class NavigationViewModel : ObservableObject
{
    private readonly NavigationService _nav;

    [ObservableProperty]
    private object? currentViewModel;

    public NavigationViewModel(NavigationService nav)
    {
        _nav = nav;
    }

    public void Navigate(object vm)
    {
        CurrentViewModel = vm;
    }

    [RelayCommand]
    private void NavigatePrepare() =>
        _nav.NavigateToPrepare();

    [RelayCommand]
    private void NavigatePlan() =>
        _nav.NavigateToPlan();

    [RelayCommand]
    private void NavigateObserve() =>
        _nav.NavigateToObserve();

    [RelayCommand]
    private void NavigateProcess() =>
        _nav.NavigateToProcess();

    [RelayCommand]
    private void NavigateVisualise() =>
        _nav.NavigateToVisualise();
}
