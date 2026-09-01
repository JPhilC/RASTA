using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Helpers;
using RASTA.App.Services;
using RASTA.Core.Calibration;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Logging;
using RASTA.Processing.Calibration;
using System.ComponentModel;

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
    private readonly ITelescopeMount _mount;
    private readonly TelescopeState _mountState;

    #region Properties ...
    // -----------------------------
    // Bindable UI properties
    // -----------------------------

    [ObservableProperty]
    private bool isCalibrationRunning;

    // Any of the three calibration steps set this while running, so the other two -
    // and Cancel - stay disabled for the duration rather than allowing overlapping runs.
    partial void OnIsCalibrationRunningChanged(bool value)
    {
        UiThread.SafeInvoke(() =>
        {
            LoadLastCalibrationCommand.NotifyCanExecuteChanged();
            CalibrateGainCommand.NotifyCanExecuteChanged();
            CaptureBaselineCommand.NotifyCanExecuteChanged();
        });
    }

    [ObservableProperty]
    private CalibrationProfile? calibration;

    // True once Calibration has an actual cold-sky baseline captured, not just a gain
    // selection - i.e. the three-step flow (Load Last Calibration / Calibrate Device
    // Gain / Capture Baseline) below has fully completed. Derived from Calibration
    // itself rather than tracked separately, so it can never drift out of sync with it.
    public bool IsCalibrated => Calibration is not null && Calibration.BaselineSpectrum.Length > 0;

    partial void OnCalibrationChanged(CalibrationProfile? value)
    {
        OnPropertyChanged(nameof(IsCalibrated));
        UiThread.SafeInvoke(() => CaptureBaselineCommand.NotifyCanExecuteChanged());
    }

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

    public double DishDiameterM
    {
        get => _settings.DishDiameterM;
        set => _settings.DishDiameterM = value;
    }

    public double FocalLengthM
    {
        get => _settings.FocalLengthM;
        set => _settings.FocalLengthM = value;
    }

    // Read-only, computed - see SettingsViewModel.BeamwidthDeg/FocalRatio. Refreshed via the
    // SettingsViewModel.PropertyChanged forwarding switch below (DishDiameterM/FocalLengthM
    // changing also raises these on SettingsViewModel itself).
    public double BeamwidthDeg => _settings.BeamwidthDeg;
    public double FocalRatio => _settings.FocalRatio;

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

    public int GainDwellSeconds
    {
        get => _settings.GainDwellSeconds;
        set
        {
            _settings.GainDwellSeconds = value;
            ValidateGainDwell(value);
            OnPropertyChanged();
        }
    }

    public int BaselineDwellSeconds
    {
        get => _settings.BaselineDwellSeconds;
        set
        {
            _settings.BaselineDwellSeconds = value;
            ValidateBaselineDwell(value);
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
        StatusBarViewModel statusBar,
        ITelescopeMount mount,
        TelescopeState mountState)
    {
        _settings = settings;
        _telescopeService = telescopeService;
        _sdrDeviceService = sdrDeviceService;
        _userPromptService = userPromptService;
        _statusBar = statusBar;
        _sdrState = sdrState;
        _calibrationService = calibrationService;
        _logger = logger;
        _mount = mount;
        _mountState = mountState;

        _sdrState.PropertyChanged += SdrStatePropertyChanged;
        _settings.PropertyChanged += SettingsPropertyChanged;

        // Initial validation
        ValidateCalibrationFrequency(CalibrationFrequencyHz);
        ValidateSampleRate(SampleRateHz);
        ValidateFftSize(FftSize);
        ValidateGainDwell(GainDwellSeconds);
        ValidateBaselineDwell(BaselineDwellSeconds);
    }

    // -----------------------------
    // IMPORTANT: Command notification override
    // -----------------------------

    protected override void NotifyCommandsOfCanExecuteChanged()
    {
        UiThread.SafeInvoke(() =>
        {
            LoadLastCalibrationCommand.NotifyCanExecuteChanged();
            CalibrateGainCommand.NotifyCanExecuteChanged();
            CaptureBaselineCommand.NotifyCanExecuteChanged();
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
            OnPropertyChanged(nameof(CanCalibrateGain));
            UiThread.SafeInvoke(() =>
            {
                CalibrateGainCommand.NotifyCanExecuteChanged();
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

            case nameof(SettingsViewModel.DishDiameterM):
                OnPropertyChanged(nameof(DishDiameterM));
                break;

            case nameof(SettingsViewModel.FocalLengthM):
                OnPropertyChanged(nameof(FocalLengthM));
                break;

            case nameof(SettingsViewModel.BeamwidthDeg):
                OnPropertyChanged(nameof(BeamwidthDeg));
                break;

            case nameof(SettingsViewModel.FocalRatio):
                OnPropertyChanged(nameof(FocalRatio));
                break;

            case nameof(SettingsViewModel.IsConnected):
                OnPropertyChanged(nameof(IsConnectedMount));
                OnPropertyChanged(nameof(CanCaptureBaseline));
                UiThread.SafeInvoke(() =>
                {
                    CaptureBaselineCommand.NotifyCanExecuteChanged();
                });
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

    private void ValidateGainDwell(int value)
    {
        ClearErrors(nameof(GainDwellSeconds));

        if (value < 1 || value > 60)
            AddError(nameof(GainDwellSeconds),
                "Gain dwell time must be between 1 and 60 seconds.");
    }

    private void ValidateBaselineDwell(int value)
    {
        ClearErrors(nameof(BaselineDwellSeconds));

        if (value < 5 || value > 300)
            AddError(nameof(BaselineDwellSeconds),
                "Baseline dwell time must be between 5 and 300 seconds.");
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
    // Calibration - split into three independent steps so a session can be resumed
    // after an interruption without redoing whichever step already completed:
    //   1. Load Last Calibration - restores a saved profile (gain, and baseline if
    //      one was captured) from disk.
    //   2. Calibrate Device Gain - runs the gain sweep against a terminator and
    //      saves a gain-only profile (empty BaselineSpectrum) immediately, so this
    //      step alone survives an app restart.
    //   3. Capture Baseline - needs a profile already started/loaded (for its gain/
    //      frequency/sample-rate/FFT size) and the mount connected (it slews to a
    //      cold-sky position); captures the baseline and updates the profile on disk.
    // -----------------------------

    private CancellationTokenSource? _calibrationCts;

    // -----------------------------
    // 1. Load Last Calibration
    // -----------------------------

    public bool CanLoadLastCalibration => !IsCalibrationRunning;

    [RelayCommand(CanExecute = nameof(CanLoadLastCalibration))]
    private async Task LoadLastCalibrationAsync()
    {
        var existing = await _calibrationService.TryLoadSavedCalibrationAsync();

        if (existing is null)
        {
            await _userPromptService.AskOkAsync("No saved calibration was found.", "Load Last Calibration");
            return;
        }

        bool hasBaseline = existing.BaselineSpectrum.Length > 0;
        Calibration = existing;

        _statusBar.CalibratedGain = hasBaseline
            ? $"Gain = {existing.GainDb:F1} dB"
            : $"Gain = {existing.GainDb:F1} dB (no baseline)";
        _statusBar.CaptureStatus = hasBaseline
            ? $"Loaded saved calibration (Gain = {existing.GainDb:F1} dB)."
            : $"Loaded saved calibration (Gain = {existing.GainDb:F1} dB) - capture a baseline to finish.";

        _logger.Info($"Loaded saved calibration (gain {existing.GainDb:F1} dB, " +
                     $"baseline {(hasBaseline ? "present" : "missing")}).");
    }

    // -----------------------------
    // 2. Calibrate Device Gain
    // -----------------------------

    public bool CanCalibrateGain => IsConnectedSdr && !IsCalibrationRunning && !HasErrors;

    [RelayCommand(CanExecute = nameof(CanCalibrateGain))]
    private async Task CalibrateGainAsync()
    {
        ISdrDevice? device = _sdrDeviceService.GetDevice();
        if (!_sdrState.IsConnected || device is null)
        {
            _statusBar.CaptureStatus = "No SDR device selected.";
            return;
        }

        await _userPromptService.AskOkAsync(
            "Press OK when ready to start gain calibration (i.e. after fitting a terminator to the LNA input).",
            "Calibrate Device Gain");

        _statusBar.CaptureStatus = "Starting gain calibration…";
        ProgressValue = 0.0;
        IsCalibrationRunning = true;
        _statusBar.IsCaptureInProgress = true;

        double frequencyHz = CalibrationFrequencyHz;
        double sampleRateHz = SampleRateHz;
        int fftSize = FftSize;
        TimeSpan dwell = TimeSpan.FromSeconds(GainDwellSeconds);

        _calibrationCts = new CancellationTokenSource();

        try
        {
            var ct = _calibrationCts.Token;

            // Re-fetch rather than reuse the reference captured above: SdrDeviceService
            // can still swap in a freshly-recreated device instance behind the scenes
            // (e.g. after a spurious USB re-enumeration event - see
            // SdrDeviceService.EnumerateDevicesAsync), and the old reference would then
            // be a stale, already-disposed object that fails deep inside RtlSdrDevice
            // with a confusing "SDR device not initialized" rather than a clear message.
            device = _sdrDeviceService.GetDevice()
                ?? throw new InvalidOperationException("SDR device is no longer available - check the USB connection and try again.");

            double gainDb = await _calibrationService.RunGainSweepAsync(
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
                ct);

            // Persisted immediately (empty BaselineSpectrum) so this step alone survives
            // an interrupted session - see the class-level comment above.
            Calibration = await _calibrationService.SaveGainOnlyCalibrationAsync(
                gainDb, frequencyHz, sampleRateHz, fftSize, device.DeviceId);

            _statusBar.CaptureStatus = $"Gain selected: {gainDb:F1} dB. Capture a baseline to finish calibration.";
            _statusBar.CalibratedGain = $"Gain = {gainDb:F1} dB (no baseline)";
            _logger.Info($"Gain sweep complete. Selected gain {gainDb:F1} dB.");
        }
        catch (OperationCanceledException)
        {
            _statusBar.CaptureStatus = "Cancelled.";
            _logger.Info("Gain calibration cancelled by user.");
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Failed.";
            _logger.Error($"Gain calibration failed: {ex.Message}");
        }
        finally
        {
            _calibrationCts?.Dispose();
            _calibrationCts = null;
            IsCalibrationRunning = false;
            _statusBar.IsCaptureInProgress = false;
        }
    }

    // -----------------------------
    // 3. Capture Baseline
    // -----------------------------

    public bool CanCaptureBaseline => Calibration != null && IsConnectedMount && !IsCalibrationRunning;

    [RelayCommand(CanExecute = nameof(CanCaptureBaseline))]
    private async Task CaptureBaselineAsync()
    {
        if (Calibration is null)
        {
            // Defensive - CanCaptureBaseline already gates the button on this.
            _statusBar.CaptureStatus = "Calibrate device gain (or load a saved calibration) first.";
            return;
        }

        if (!_settings.IsConnected)
        {
            // Defensive - CanCaptureBaseline already gates the button on this, but the
            // command could still be invoked directly (e.g. the mount dropping the
            // connection between the button becoming enabled and the click landing).
            _statusBar.CaptureStatus = "Telescope must be connected to capture a baseline.";
            return;
        }

        ISdrDevice? device = _sdrDeviceService.GetDevice();
        if (!_sdrState.IsConnected || device is null)
        {
            _statusBar.CaptureStatus = "No SDR device selected.";
            return;
        }

        // Gain/frequency/sample-rate/FFT size come from the profile started by
        // Calibrate Device Gain (or a loaded one) - a baseline must be built with
        // exactly the settings the gain was chosen for.
        double frequencyHz = Calibration.CenterFrequencyHz;
        double sampleRateHz = Calibration.SampleRateHz;
        int fftSize = Calibration.FftSize;
        double gainDb = Calibration.GainDb;
        TimeSpan baselineDwell = TimeSpan.FromSeconds(BaselineDwellSeconds);

        _statusBar.CaptureStatus = "Starting baseline capture…";
        ProgressValue = 0.0;
        IsCalibrationRunning = true;
        _statusBar.IsCaptureInProgress = true;

        _calibrationCts = new CancellationTokenSource();
        bool baselineCaptured = false;

        try
        {
            var ct = _calibrationCts.Token;

            // ---- Reconnect antenna, then pick a cold-sky position ----
            await _userPromptService.AskOkAsync(
                "Press OK after you have reconnected your antenna for the cold-sky baseline capture.",
                "Capture Baseline");

            // Azimuths already offered/rejected in this attempt - grows every time
            // Recalculate is clicked (all currently-shown positions) or a slewed-to position
            // is rejected as obstructed (just that one), so neither repeats a dud suggestion.
            // See ColdSkyLocator.FindCandidates' excludeAzimuthsDeg.
            var excludedAzimuthsDeg = new List<double>();

            IReadOnlyList<ColdSkyCandidate> GenerateCandidates() => ColdSkyLocator.FindCandidates(
                SiteLatitudeDeg, SiteLongitudeDeg, DateTime.UtcNow, HorizonLimitDeg,
                excludeAzimuthsDeg: excludedAzimuthsDeg);

            IReadOnlyList<ColdSkyCandidate> Recalculate(IReadOnlyList<ColdSkyCandidate> currentlyShown)
            {
                excludedAzimuthsDeg.AddRange(currentlyShown.Select(c => c.AzimuthDeg));
                return GenerateCandidates();
            }

            var candidates = GenerateCandidates();
            ColdSkyCandidate? chosen = null;

            // Loop until the user confirms the actual, physically-slewed-to position is
            // acceptable (e.g. not pointed at a building) - "No" sends them back to the
            // picker, excluding the rejected position, rather than capturing a baseline
            // against whatever's in the way.
            while (true)
            {
                chosen = await _userPromptService.PickColdSkyLocationAsync(candidates, Recalculate);
                if (chosen is null)
                {
                    _statusBar.CaptureStatus = "Cancelled.";
                    _logger.Info("Baseline capture cancelled - no cold-sky position chosen.");
                    return;
                }

                // ---- Slew to the chosen position ----
                _statusBar.CaptureStatus = "Slewing to cold-sky position…";
                if (_mount.Mode == CoordinateMode.Equatorial)
                {
                    await _mount.SlewToRaDecAsync(chosen.RightAscensionHours, chosen.DeclinationDeg);
                }
                else
                {
                    await _mount.SlewToAzAltAsync(chosen.AzimuthDeg, chosen.ElevationDeg);
                }
                if (!await WaitForSlewCompleteAsync(ct))
                    return; // timed out - message already shown

                _statusBar.CaptureStatus = "Confirming cold-sky position…";
                bool positionOk = await _userPromptService.AskYesNoAsync(
                    "Is the telescope's current position clear of obstructions (e.g. no building, tree, or " +
                    "other horizon feature in the way)?\n\n" +
                    "Choose No to go back and pick a different position instead.",
                    "Confirm Cold-Sky Position");

                if (positionOk)
                    break;

                _logger.Info($"Cold-sky position Az {chosen.AzimuthDeg:F1}°/Alt {chosen.ElevationDeg:F1}° rejected as obstructed - returning to picker.");
                excludedAzimuthsDeg.Add(chosen.AzimuthDeg);
                candidates = GenerateCandidates();
            }

            // ---- Capture the cold-sky baseline ----
            // Re-fetch again - the confirm-position loop above can run for several minutes
            // (picker, slew, confirmation prompt, possibly repeated), which is exactly the
            // kind of window a stale device reference would go unnoticed in otherwise.
            device = _sdrDeviceService.GetDevice()
                ?? throw new InvalidOperationException("SDR device is no longer available - check the USB connection and try again.");

            Calibration = await _calibrationService.CaptureColdSkyBaselineAsync(
                device,
                frequencyHz,
                sampleRateHz,
                gainDb,
                baselineDwell,
                fftSize,
                chosen,
                SiteLatitudeDeg,
                SiteLongitudeDeg,
                SiteElevationM,
                (msg, pct) =>
                {
                    _statusBar.CaptureStatus = msg;
                    _statusBar.CaptureProgress = pct;
                },
                ct);

            baselineCaptured = true;
            _statusBar.CaptureStatus = $"Done. Gain = {Calibration.GainDb:F1} dB";
            _statusBar.CalibratedGain = $"Gain = {Calibration.GainDb:F1} dB";
            _logger.Info($"Baseline capture complete. Gain {Calibration.GainDb:F1} dB, " +
                         $"cold-sky Az {chosen.AzimuthDeg:F1}°/Alt {chosen.ElevationDeg:F1}°.");
        }
        catch (OperationCanceledException)
        {
            _statusBar.CaptureStatus = "Cancelled.";
            _logger.Info("Baseline capture cancelled by user.");
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Failed.";
            _logger.Error($"Baseline capture failed: {ex.Message}");
        }
        finally
        {
            // Return the mount to its home position before finishing up, the same way
            // CaptureViewModel.CaptureSweepAsync does at the end of a sweep - the cold-sky
            // capture leaves the mount pointed away from home, and there's no reason to
            // leave it there, whether the capture succeeded, failed, or was cancelled
            // after a slew already happened. Tracking is switched off first, mirroring
            // CaptureViewModel's ordering - this mount setup has already been seen to refuse
            // a slew while tracking is active (see the "SlewToAltAz is not allowed when
            // tracking is True" Alpaca error in the logs).
            try
            {
                if (await _mount.GetTrackingAsync() && await _mount.GetCanSetTrackingAsync())
                {
                    await _mount.SetTrackingAsync(false);
                }
                if (await _mount.GetCanFindHomeAsync())
                {
                    _statusBar.CaptureStatus = "Returning telescope to home position…";
                    await _mount.FindHomeAsync();

                    // Only claim success once the mount is actually home - leave a prior
                    // "Cancelled."/"Failed." status alone rather than papering over it.
                    if (baselineCaptured)
                        _statusBar.CaptureStatus = "Baseline capture complete.";
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to return telescope to home position after baseline capture: {ex.Message}");
            }

            _calibrationCts?.Dispose();
            _calibrationCts = null;
            IsCalibrationRunning = false;
            _statusBar.IsCaptureInProgress = false;
        }
    }

    /// <summary>
    /// Polls the mount's IsSlewing flag every 500ms until it clears, with a 30s timeout -
    /// same pattern CaptureViewModel.CaptureSweepAsync uses to wait out a slew. Returns false
    /// (after showing a warning) if the slew times out, true once it completes normally.
    /// </summary>
    private async Task<bool> WaitForSlewCompleteAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            while (_mountState.IsSlewing)
            {
                await Task.Delay(500, timeoutCts.Token);
            }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // ct was not cancelled - this was our timeout firing.
            await _userPromptService.AskOkAsync(
                "Slew timed out after 30 seconds. Baseline capture will be abandoned.",
                "Warning");
            _statusBar.CaptureStatus = "Slew timed out.";
            return false;
        }
    }

    [RelayCommand]
    private void CancelCalibration()
    {
        _calibrationCts?.Cancel();
        _statusBar.CaptureStatus = "Cancelled";
        _statusBar.IsCaptureInProgress = false;

        // Reflect whatever's actually still loaded/started - cancelling Capture Baseline
        // shouldn't discard a gain already selected (or a previously saved calibration).
        _statusBar.CalibratedGain = Calibration is null
            ? "Uncalibrated"
            : Calibration.BaselineSpectrum.Length > 0
                ? $"Gain = {Calibration.GainDb:F1} dB"
                : $"Gain = {Calibration.GainDb:F1} dB (no baseline)";
    }

}
