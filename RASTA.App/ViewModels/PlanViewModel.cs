using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Telescope;
using RASTA.Processing.Planning;

namespace RASTA.App.ViewModels;

public partial class PlanViewModel : ObservableObject
{
    private readonly SweepPlanner _planner;

    [ObservableProperty]
    private TargetRange? range;

    [ObservableProperty]
    private List<TargetPoint>? plannedPoints;

    public PlanViewModel(SweepPlanner planner)
    {
        _planner = planner;
    }

    [RelayCommand]
    private void BuildSweep()
    {
        if (Range is null)
            return;

        PlannedPoints = _planner.BuildSweep(Range).ToList();
    }
}
