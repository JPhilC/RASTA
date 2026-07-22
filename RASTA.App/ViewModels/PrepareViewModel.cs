using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Calibration;
using RASTA.Core.Capture;
using RASTA.Infrastructure.Logging;
using RASTA.Processing.Spectral;
using System.ComponentModel;

namespace RASTA.App.ViewModels;

public partial class PrepareViewModel : ObservableObject
{
    private readonly SpectrumMath _math;
    private readonly RastaLogger _logger;
    private readonly SettingsViewModel _settings;

    [ObservableProperty]
    private CalibrationProfile? calibration;

    [ObservableProperty]
    private bool isCalibrated;

    #region Pass through properties to SettingsViewModel ...
    public bool IsConnected 
    {
        get => _settings.IsConnected;
    }
    
    public double SiteLatitudeDeg
    {
        get => _settings.SiteLatitudeDeg;
        set => _settings.SiteLatitudeDeg = value;
    }

    public double SiteLongitudeDeg
    {
        get => _settings.SiteLongitudeDeg;
        set => _settings.SiteLongitudeDeg = value;
    }

    public double SiteElevationM
    {
        get => _settings.SiteElevationM;
        set => _settings.SiteElevationM = value;
    }
    
    #endregion

    public PrepareViewModel(SpectrumMath math, RastaLogger logger, SettingsViewModel settings)
    {
        _math = math;
        _logger = logger;
        _settings = settings;

        _settings.PropertyChanged += SettingsOnPropertyChanged;
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SiteLatitudeDeg):
                OnPropertyChanged(nameof(SiteLatitudeDeg));
                break;

            case nameof(SettingsViewModel.SiteLongitudeDeg):
                OnPropertyChanged(nameof(SiteLongitudeDeg));
                break;

            case nameof(SettingsViewModel.SiteElevationM):
                OnPropertyChanged(nameof(SiteElevationM));
                break;

            case nameof(SettingsViewModel.IsConnected):
                OnPropertyChanged(nameof(IsConnected));
                break;

                // Add more if needed
        }
    }

    public IRelayCommand ConnectTelescopeCommand => _settings.ConnectTelescopeCommand;
    public IRelayCommand DisconnectTelescopeCommand => _settings.DisconnectTelescopeCommand;

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
