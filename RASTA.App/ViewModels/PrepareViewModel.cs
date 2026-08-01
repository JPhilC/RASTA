using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using RASTA.Core.Calibration;
using RASTA.Core.Sdr;
using RASTA.Infrastructure.Logging;
using RASTA.Processing.Calibration;
using System.ComponentModel;
using System.Windows;

namespace RASTA.App.ViewModels;

public partial class PrepareViewModel : ViewModelBase
{
    private readonly RastaLogger _logger;
    private readonly SettingsViewModel _settings;
    private readonly TelescopeService _telescopeService;
    private readonly SdrDeviceService _sdrDeviceService;
    private readonly SdrState _sdrState;
    private readonly CalibrationService _calibrationService;
    private readonly IUserPromptService _userPromptService;
    private readonly StatusBarViewModel _statusBar;

    #region Properties ...
    // -----------------------------
    // Bindable UI properties
    // -----------------------------

    [ObservableProperty]
    private bool isCalibrationRunning;

    [ObservableProperty]
    private CalibrationProfile? calibration;

    [ObservableProperty]
    private bool isCalibrated;

    [ObservableProperty]
    private double progressValue;

    // -----------------------------
    // Pass-through properties
    // -----------------------------

    public bool IsConnectedMount => _settings.IsConnected;
    public bool IsConnectedSdr => _sdrState.IsConnected;

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

    public double SlewRateDegPerSec
    {
        get => _settings.SlewRateDegPerSec;
        set => _settings.SlewRateDegPerSec = value;
    }

    public double HorizonLimitDeg
    {
        get => _settings.HorizonLimitDeg;
        set => _settings.HorizonLimitDeg = value;
    }

    public double CalibrationFrequencyHz
    {
        get => _settings.CalibrationFrequencyHz;
        set
        {
            _settings.CalibrationFrequencyHz = value;
            ValidateCalibrationFrequency(value);
            OnPropertyChanged();
        }
    }

    public double SampleRateHz
    {
        get => _settings.SampleRateHz;
        set
        {
            _settings.SampleRateHz = value;
            ValidateSampleRate(value);
            OnPropertyChanged();
        }
    }

    public int FftSize
    {
        get => _settings.FftSize;
        set
        {
            _settings.FftSize = value;
            ValidateFftSize(value);
            OnPropertyChanged();
        }
    }

    public int CalibrationDwellSeconds
    {
        get => _settings.CalibrationDwellSeconds;
        set
        {
            _settings.CalibrationDwellSeconds = value;
            ValidateDwell(value);
            OnPropertyChanged();
        }
    }

    #endregion

    // -----------------------------
    // Constructor
    // -----------------------------

    public PrepareViewModel(
        SettingsViewModel settings,
        TelescopeService telescopeService,
        SdrDeviceService sdrDeviceService,
        SdrState sdrState,
        CalibrationService calibrationService,
        RastaLogger logger,
        IUserPromptService userPromptService,
        StatusBarViewModel statusBar)
    {
        _settings = settings;
        _telescopeService = telescopeService;
        _sdrDeviceService = sdrDeviceService;
        _userPromptService = userPromptService;
        _statusBar = statusBar;
        _sdrState = sdrState;
        _calibrationService = calibrationService;
        _logger = logger;

        _sdrState.PropertyChanged += SdrStatePropertyChanged;
        _settings.PropertyChanged += SettingsPropertyChanged;

        // Initial validation
        ValidateCalibrationFrequency(CalibrationFrequencyHz);
        ValidateSampleRate(SampleRateHz);
        ValidateFftSize(FftSize);
        ValidateDwell(CalibrationDwellSeconds);
    }

    // -----------------------------
    // IMPORTANT: Command notification override
    // -----------------------------

    protected override void NotifyCommandsOfCanExecuteChanged()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RunCalibrationCommand.NotifyCanExecuteChanged();
        });
    }

    // -----------------------------
    // SDR state changes
    // -----------------------------
    private void SdrStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"SDR state changed: {e.PropertyName}");
        if (e.PropertyName == nameof(SdrState.IsConnected))
        {
            OnPropertyChanged(nameof(IsConnectedSdr));
            OnPropertyChanged(nameof(CanRunCalibration));
            Application.Current.Dispatcher.Invoke(() =>
            {
                RunCalibrationCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void SettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
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
                OnPropertyChanged(nameof(IsConnectedMount));
                break;
        }
    }

    // -----------------------------
    // Validation logic
    // -----------------------------

    private void ValidateCalibrationFrequency(double value)
    {
        ClearErrors(nameof(CalibrationFrequencyHz));

        if (value < 1.0e6 || value > 2.0e9)
            AddError(nameof(CalibrationFrequencyHz),
                "Calibration frequency must be between 1 GHz and 2 GHz.");
    }

    private void ValidateSampleRate(double value)
    {
        ClearErrors(nameof(SampleRateHz));

        double[] allowed = { 1.024e6, 2.048e6, 2.4e6 };

        if (!allowed.Contains(value))
            AddError(nameof(SampleRateHz),
                "Sample rate must be one of: 1.024 MHz, 2.048 MHz, 2.4 MHz.");
    }

    private void ValidateFftSize(int value)
    {
        ClearErrors(nameof(FftSize));

        bool isPowerOfTwo = (value & (value - 1)) == 0;

        if (!isPowerOfTwo)
            AddError(nameof(FftSize), "FFT size must be a power of two.");
    }

    private void ValidateDwell(int value)
    {
        ClearErrors(nameof(CalibrationDwellSeconds));

        if (value < 1 || value > 60)
            AddError(nameof(CalibrationDwellSeconds),
                "Dwell time must be between 1 and 60 seconds.");
    }

    // -----------------------------
    // Telescope connect/disconnect
    // -----------------------------

    [RelayCommand]
    private async Task ConnectTelescopeAsync()
    {
        await _settings.ConnectTelescopeAsync();

        if (_settings.IsConnected)
        {
            _telescopeService.Start();
            _logger.Info("Telescope telemetry started.");
        }
    }

    [RelayCommand]
    private async Task DisconnectTelescopeAsync()
    {
        await _settings.DisconnectTelescopeAsync();
        _telescopeService.Stop();
        _logger.Info("Telescope telemetry stopped.");
    }

    // -----------------------------
    // Gain-sweep calibration
    // -----------------------------

    private CancellationTokenSource? _calibrationCts;


    public bool CanRunCalibration =>
        IsConnectedSdr && !HasErrors;

    [RelayCommand(CanExecute = nameof(CanRunCalibration))]
    private async Task RunCalibrationAsync()
    {
        ISdrDevice? device = _sdrDeviceService.GetDevice();
        if (!_sdrState.IsConnected || device is null)
        {
            _statusBar.CaptureStatus = "No SDR device selected.";
            return;
        }

        // ---------------------------------------------------------
        // 1. Check for previously saved calibration
        // ---------------------------------------------------------
        var existing = await _calibrationService.TryLoadSavedCalibrationAsync();

        if (existing != null)
        {
            bool reuse = await _userPromptService.AskYesNoAsync(
                $"A previous calibration exists (Gain = {existing.GainDb:F1} dB).\n\n" +
                $"Calibration was performed at {existing.CenterFrequencyHz / 1e6:F3} MHz,\n" +
                $"Sample Rate = {existing.SampleRateHz / 1e6:F3} MHz,\n" +
                $"FFT Size = {existing.FftSize}, " +
                $"On {existing.TimestampUtc.ToLocalTime():g}.\n\n" +
                $"Do you want to reuse it instead of running a new calibration?",
                "Reuse Calibration");

            if (reuse)
            {
                Calibration = existing;
                IsCalibrated = true;
                _statusBar.CaptureStatus = $"Reusing Gain = {existing.GainDb:F1} dB";
                _statusBar.CalibratedGain = $"Gain = {existing.GainDb:F1} dB";
                _logger.Info("Reused saved calibration.");
                return;
            }
            else
            {
                await _userPromptService.AskOkAsync(
                    "Press OK when ready to start a new calibration (i.e. after fitting terminator).",
                    "Calibration");
            }
        }

        // ---------------------------------------------------------
        // 2. Run a new calibration
        // ---------------------------------------------------------
        _statusBar.CaptureStatus = "Starting calibration…";
        ProgressValue = 0.0;
        IsCalibrated = false;
        IsCalibrationRunning = true;
        _statusBar.IsCaptureInProgress = true;

        double frequencyHz = CalibrationFrequencyHz;
        double sampleRateHz = SampleRateHz;
        int fftSize = FftSize;
        TimeSpan dwell = TimeSpan.FromSeconds(CalibrationDwellSeconds);

        _calibrationCts = new CancellationTokenSource();

        try
        {
            Calibration = await _calibrationService.RunCalibrationAsync(
                device,
                frequencyHz,
                sampleRateHz,
                dwell,
                fftSize,
                (msg, pct) =>
                {
                    _statusBar.CaptureStatus = msg;
                    _statusBar.CaptureProgress = pct;
                },
                _calibrationCts.Token);

            IsCalibrated = true;
            _statusBar.CaptureStatus = $"Done. Gain = {Calibration.GainDb:F1} dB";
            _statusBar.CalibratedGain = $"Gain = {Calibration.GainDb:F1} dB";
            _logger.Info($"Calibration complete. Selected gain {Calibration.GainDb:F1} dB.");

            await _userPromptService.AskOkAsync(
            "Press OK after you have reconnected your antenna.",
            "Calibration");

        }
        catch (OperationCanceledException)
        {
            _statusBar.CaptureStatus = "Cancelled.";
            _logger.Info("Calibration cancelled by user.");
            _statusBar.CalibratedGain = $"Uncalibrated";
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Failed.";
            _logger.Error($"Calibration failed: {ex.Message}");
            _statusBar.CalibratedGain = $"Uncalibrated";
        }
        finally
        {
            _calibrationCts?.Dispose();
            _calibrationCts = null;
            IsCalibrationRunning = false;
            _statusBar.IsCaptureInProgress = false;


        }
    }

    [RelayCommand]
    private void CancelCalibration()
    {
        _calibrationCts?.Cancel();
        _statusBar.CaptureStatus = "Cancelled";
        _statusBar.IsCaptureInProgress = false;
        _statusBar.CalibratedGain = $"Uncalibrated";
    }

}
