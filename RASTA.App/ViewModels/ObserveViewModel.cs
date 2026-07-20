using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Capture;
using RASTA.Core.Calibration;
using RASTA.Core.Telescope;
using RASTA.Processing.Capture;

namespace RASTA.App.ViewModels;

public partial class ObserveViewModel : ObservableObject
{
    private readonly ITelescopeMount _mount;
    private readonly ObservationCaptureService _capture;

    [ObservableProperty]
    private CalibrationProfile? calibration;

    [ObservableProperty]
    private ObservationRecord? lastObservation;

    [ObservableProperty]
    private List<ObservationRecord>? sweepObservations;

    public ObserveViewModel(
        ITelescopeMount mount,
        ObservationCaptureService capture)
    {
        _mount = mount;
        _capture = capture;
    }

    public void LoadCalibration(CalibrationProfile profile)
    {
        Calibration = profile;
    }

    [RelayCommand]
    private async Task CaptureSingleAsync()
    {
        if (Calibration is null)
            throw new InvalidOperationException("Calibration not loaded.");

        await _mount.SlewToAzElAsync(180, 45);

        LastObservation = await _capture.CaptureAsync(
            new TargetPoint { AzimuthDeg = 180, ElevationDeg = 45 },
            Calibration,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
    }

    [RelayCommand]
    private async Task CaptureSweepAsync(IEnumerable<TargetPoint> points)
    {
        if (Calibration is null)
            throw new InvalidOperationException("Calibration not loaded.");

        var list = new List<ObservationRecord>();

        foreach (var p in points)
        {
            await _mount.SlewToTargetAsync(p);

            var obs = await _capture.CaptureAsync(
                p,
                Calibration,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            list.Add(obs);
        }

        SweepObservations = list;
    }
}
