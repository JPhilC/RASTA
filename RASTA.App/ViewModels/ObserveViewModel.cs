using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using RASTA.Core.Calibration;
using RASTA.Core.Capture;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Processing.Planning;
using System.Windows;
using System.Windows.Threading;

namespace RASTA.App.ViewModels;

public partial class ObserveViewModel : ObservableObject
{
    private DispatcherTimer? _progressTimer;

    private readonly ITelescopeMount _mount;
    private readonly TelescopeState _mountState;
    private readonly SdrDeviceService _sdrDeviceService;
    private readonly SdrState _sdrState;
    private readonly ISdrDevice? _device;
    private readonly SettingsViewModel _settings;
    private readonly CalibrationService _calibrationService;
    private readonly SweepPlanner _planner;
    private readonly FitsFileIo _fitsFileWriter;
    private readonly StatusBarViewModel _statusBar;
    private CancellationTokenSource? _sweepCts;
    
    private CapturePlan? _activePlan;

    public CapturePlan? ActivePlan
    {
        get => _activePlan;
        set
        {
            if (SetProperty(ref _activePlan, value))
            {
                OnPropertyChanged(nameof(PlanName));
            }
        }
    }

    public string PlanName => _activePlan?.FriendlyName ?? "No Plan";

    public bool CanCaptureSweep => _activePlan != null
        && _device != null 
        && _calibrationService.CurrentCalibration != null
        && _mountState.IsConnected
        && _sdrState.IsConnected
        && _activePlan.PlanType != PlanType.Drift;

    [ObservableProperty]
    private bool isSweepCaptureRunning;


    public bool CanDriftCapture => _activePlan != null
        && _device != null
        && _calibrationService.CurrentCalibration != null
        && _sdrState.IsConnected
        && _activePlan.PlanType == PlanType.Drift;

    [ObservableProperty]
    private bool isDriftCaptureRunning;

    [ObservableProperty]
    private ObservationRecord? lastObservation;

    [ObservableProperty]
    private bool isBusy;


    public ObserveViewModel(
        SettingsViewModel settingsViewModel,
        ITelescopeMount mount,
        TelescopeState mountState,
        SdrDeviceService sdrDeviceService,
        SdrState sdrState,
        CalibrationService calibrationService,
        SweepPlanner planner,
        FitsFileIo fitsFileWriter,
        StatusBarViewModel statusBarViewModel)
    {
        _settings = settingsViewModel;
        _mount = mount;
        _mountState = mountState;
        _calibrationService = calibrationService;
        _statusBar = statusBarViewModel;
        _planner = planner;
        _sdrDeviceService = sdrDeviceService;
        _fitsFileWriter = fitsFileWriter;
        _sdrState = sdrState;
        _device = sdrDeviceService.GetDevice();

        _mountState.PropertyChanged += MountState_PropertyChanged;
        _sdrState.PropertyChanged += SdrState_PropertyChanged;

    }

    private void MountState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TelescopeState.IsConnected))
        {
            OnPropertyChanged(nameof(CanCaptureSweep));
            OnPropertyChanged(nameof(CanDriftCapture));
        }
    }

    private void SdrState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SdrState.IsConnected))
        {
            OnPropertyChanged(nameof(CanCaptureSweep));
            OnPropertyChanged(nameof(CanDriftCapture));
        }
    }


    [RelayCommand]
    private async Task CaptureSweepAsync()
    {
        if (ActivePlan is null || _calibrationService.CurrentCalibration is null)
        {
            MessageBox.Show("No plan or calibration profile selected.");
            return;
        }

        if (!_mount.IsConnected)
        {
            MessageBox.Show("Mount is not connected.");
            return;
        }

        if (_device == null)
        {
            MessageBox.Show("Device is not connected.");
            return;
        }

        DateTime startTime = DateTime.UtcNow;

        // Build the target points for the ActivePlan.
        // This will return a list of TargetPoint objects that represent the points to capture.
        // or an error message if the plan would send the mount below the horizon limit.
        SweepPlanResult sweepPlanResult = _planner.BuildSweep(
            ActivePlan,
            startTime,
            ActivePlan.DwellTime,
            ActivePlan.SettleTimeSeconds,
            _settings.SiteLatitudeDeg,
            _settings.SiteLongitudeDeg,
            _settings.HorizonLimitDeg, // minElevationDeg
            _settings.SlewRateDegPerSec);


        if (!sweepPlanResult.Success)
        {
            MessageBox.Show(sweepPlanResult.ErrorMessage);
            return;
        }


        _sweepCts = new CancellationTokenSource();
        var ct = _sweepCts.Token;
        try
        {
            IsBusy = true;

            double gainDb = _calibrationService.CurrentCalibration.GainDb;
            int filesPerPoint = ActivePlan.FilesPerPoint;
            int targetIndex = 0;

            foreach (var target in sweepPlanResult.Points)
            {
                ct.ThrowIfCancellationRequested();

                // -----------------------------
                // Stage 1 — Slewing
                // -----------------------------
                _statusBar.CaptureStatus = $"Slewing to pos {targetIndex + 1}";
                _statusBar.IsCaptureInProgress = false;

                if (target.Mode == CoordinateMode.Equatorial)
                    await _mount.SlewToRaDecAsync(target.RightAscensionHours, target.DeclinationDeg);
                else
                    await _mount.SlewToAzAltAsync(target.AzimuthDeg, target.ElevationDeg);

                // Need to wait for mount to finish slewing and then wait for the settle time.

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                try
                {
                    while (_mountState.IsSlewing)
                    {
                        await Task.Delay(500, timeoutCts.Token);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // ct was not cancelled — this was our timeout firing
                    MessageBox.Show("Slew timed out after 30 seconds. Capture will be abandoned.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }


                var dwellSeconds = ActivePlan.DwellTime.TotalSeconds / filesPerPoint;
                var sampleRateHz = ActivePlan.SampleRate;
                var frequencyHz = ActivePlan.CenterFrequency;
                // Compute sample count safely
                uint sampleCount = (uint)Math.Ceiling(sampleRateHz * dwellSeconds);

                // Capture multiple files at this point
                for (int i = 0; i < filesPerPoint; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // -----------------------------
                    // Stage 2 — Capturing
                    // -----------------------------
                    _statusBar.CaptureStatus = $"Capturing pos {targetIndex + 1} file {i + 1}";
                    StartProgressTimer(dwellSeconds);

                    var rawIq = await _device.CaptureRawIqAsync(
                        frequencyHz,
                        sampleRateHz,
                        gainDb,
                        sampleCount,
                        ct);

                    StopProgressTimer();

                    string fullPath = FitsPathBuilder.BuildSweepFilePath("sweep", startTime, ActivePlan.CenterFrequency, target.ToString(), i + 1, filesPerPoint);

                    var meta = new FitsFileMetaData
                    {
                        Origin = "RTL-SDR",
                        DataFormat = "UINT8_IQ",
                        CentFreqHz = frequencyHz,
                        SampFreqHz = sampleRateHz,
                        GainDb = gainDb,
                        ObservationDate = DateTime.UtcNow,
                        DwellTimeSec = dwellSeconds
                    };

                    // -----------------------------
                    // Stage 3 — Saving
                    // -----------------------------
                    _statusBar.CaptureStatus = $"Saving pos {targetIndex + 1} file {i + 1}";
                    StartProgressTimer(1.0); // animate saving for 1 second
                    
                    _fitsFileWriter.WriteRawIq(fullPath, rawIq, meta);

                    StopProgressTimer();

                }
            }

            MessageBox.Show("Sweep complete.");
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Sweep cancelled.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sweep Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartProgressTimer(double durationSeconds)
    {
        _statusBar.CaptureProgress = 0;
        _statusBar.IsCaptureInProgress = true;

        double elapsed = 0;
        double interval = 0.1; // 100ms

        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(interval)
        };

        _progressTimer.Tick += (s, e) =>
        {
            elapsed += interval;
            _statusBar.CaptureProgress = Math.Min(elapsed / durationSeconds, 1.0);

            if (elapsed >= durationSeconds)
            {
                StopProgressTimer();
            }
        };

        _progressTimer.Start();
    }

    private void StopProgressTimer()
    {
        if (_progressTimer != null)
        {
            _progressTimer.Stop();
            _progressTimer = null;
        }

        _statusBar.IsCaptureInProgress = false;
        _statusBar.CaptureProgress = 0;
    }


    // -------------------------------------------------------
    // Slew to a specific target point
    // -------------------------------------------------------

    [RelayCommand]
    private async Task SlewToTargetAsync(TargetPoint target)
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            if (target.Mode == CoordinateMode.Equatorial)
                await _mount.SlewToRaDecAsync(target.RightAscensionHours, target.DeclinationDeg);
            else
                await _mount.SlewToAzAltAsync(target.AzimuthDeg, target.ElevationDeg);
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Slew to a fixed Az/El (AltAz mode)
    // -------------------------------------------------------

    private async Task SlewToAzAltAsync(double azDeg, double altDeg)
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            await _mount.SlewToAzAltAsync(azDeg, altDeg);
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Slew to RA/Dec (Equatorial mode)
    // -------------------------------------------------------

    private async Task SlewToRaDecAsync(double raHours, double decDeg)
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            await _mount.SlewToRaDecAsync(raHours, decDeg);
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Abort Slew
    // -------------------------------------------------------

    [RelayCommand]
    private async Task AbortSlewAsync()
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            await _mount.AbortSlewAsync();
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Capture observation
    // -------------------------------------------------------


}
