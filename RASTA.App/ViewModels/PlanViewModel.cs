using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Telescope;
using RASTA.Processing.Planning;
using System.ComponentModel;

namespace RASTA.App.ViewModels;

public partial class PlanViewModel : ObservableObject
{
    public SettingsViewModel Settings { get; }

    private readonly SweepPlanner _planner;

    [ObservableProperty]
    private TargetRange? range;

    [ObservableProperty]
    private List<TargetPoint>? plannedPoints;

    public PlanViewModel(SweepPlanner planner, SettingsViewModel settings)
    {
        _planner = planner;
        Settings = settings;
        Settings.PropertyChanged += SettingsOnPropertyChanged;

        Range = new TargetRange
        {
            Mode = settings.Mode,
            StepDeg = 5.0, // sensible default
            DwellTime = TimeSpan.FromSeconds(1)
        };
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Mode))
        {
            // Notify the UI that Settings.CoordinateMode changed
            OnPropertyChanged(nameof(Settings));
        }
    }


    [RelayCommand]
    private void BuildSweep()
    {
        if (Range is null)
            return;

        // Ensure the range mode matches the current settings
        Range.Mode = Settings.Mode;

        PlannedPoints = _planner.BuildSweep(Range).ToList();
    }

}
