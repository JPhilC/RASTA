using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Calibration;
using RASTA.Core.Capture;
using RASTA.Processing.Spectral;
using RASTA.Infrastructure.Logging;

namespace RASTA.App.ViewModels;

public partial class PrepareViewModel : ObservableObject
{
    private readonly SpectrumMath _math;
    private readonly RastaLogger _logger;

    [ObservableProperty]
    private CalibrationProfile? calibration;

    [ObservableProperty]
    private bool isCalibrated;

    public PrepareViewModel(SpectrumMath math, RastaLogger logger)
    {
        _math = math;
        _logger = logger;
    }

    [RelayCommand]
    private void BuildCalibration(ObservationRecord noiseRecord)
    {
        var baseline = _math.SubtractBaseline(noiseRecord.AveragedSpectrum);
        var smoothed = _math.Smooth(baseline);

        Calibration = new CalibrationProfile
        {
            NoiseSpectrum = smoothed,
            TimestampUtc = DateTime.UtcNow
        };

        IsCalibrated = true;
        _logger.Info("Calibration profile built.");
    }
}
