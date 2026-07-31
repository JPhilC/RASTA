using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using RASTA.Core.Capture;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Services;
using RASTA.Processing.Planning;
using System.Collections.Concurrent;
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
    private readonly IFftEngine _fftEngine;
    private readonly UserOptionsService _optionsService;

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

    public SpectrumViewModel SpectrumVm { get; private set; }

    public ObserveViewModel(
        SettingsViewModel settingsViewModel,
        UserOptionsService userOptionsService,
        ITelescopeMount mount,
        TelescopeState mountState,
        SdrDeviceService sdrDeviceService,
        SdrState sdrState,
        CalibrationService calibrationService,
        SweepPlanner planner,
        FitsFileIo fitsFileWriter,
        StatusBarViewModel statusBarViewModel,
        IFftEngine fftEngine)
    {
        _settings = settingsViewModel;
        _optionsService = userOptionsService;
        _mount = mount;
        _mountState = mountState;
        _calibrationService = calibrationService;
        _statusBar = statusBarViewModel;
        _planner = planner;
        _sdrDeviceService = sdrDeviceService;
        _fitsFileWriter = fitsFileWriter;
        _sdrState = sdrState;
        _fftEngine = fftEngine;
        _device = sdrDeviceService.GetDevice();

        SpectrumVm = new SpectrumViewModel(1024, 1420e6, 2.4e6); // default values; will be updated when calibration is loaded

        _mountState.PropertyChanged += MountState_PropertyChanged;
        _sdrState.PropertyChanged += SdrState_PropertyChanged;
        // _device.RawIqChunkAvailable += OnChunk;

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

    #region Live Spectrum Charting

    private readonly ConcurrentQueue<byte[]> _chunkProcessingQueue = new();

    private CancellationTokenSource? _chunkWorkerCts;

    private int fftSize = 1024;

    private double[]? calibrationBaselineSpectrum;

    private int chunkCount = 0;

    private double[] running = new double[1024];

    private bool captureInProgress = false;

    private void OnChunk(byte[] chunk)
    {
        // FAST: enqueue chunk for DSP worker
        _chunkProcessingQueue.Enqueue(chunk);
    }

    private async Task ChunkWorker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_chunkProcessingQueue.TryDequeue(out var chunk))
            {
                ProcessChunk(chunk);
            }
            else
            {
                await Task.Delay(1, ct); // yield
            }
        }
    }

    private void ProcessChunk(byte[] chunk)
    {
        if (!captureInProgress || calibrationBaselineSpectrum == null)
            return;

        var spectrum = _fftEngine.ComputeSpectrum(chunk, fftSize);

        var corrected = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            double spectrumDb = 10 * Math.Log10(spectrum[i] + 1e-12);
            double baselineDb = 10 * Math.Log10(calibrationBaselineSpectrum[i] + 1e-12);
            corrected[i] = spectrumDb - baselineDb;
        }

        for (int i = 0; i < fftSize; i++)
            running[i] = ((running[i] * chunkCount) + corrected[i]) / (chunkCount + 1);

        chunkCount++;

        // UI update must be dispatched
        App.Current.Dispatcher.Invoke(() =>
        {
            SpectrumVm.UpdateSpectrum(running.ToArray());
        });
    }



    #endregion

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

        // Enable tracking if the plan requires it and the mount supports it.
        if (ActivePlan.TrackingEnabled && await _mount.GetCanSetTrackingAsync())
        {
            await _mount.SetTrackingAsync(true);
        }

        _sweepCts = new CancellationTokenSource();
        var ct = _sweepCts.Token;
        try
        {
            IsBusy = true;

            double gainDb = _calibrationService.CurrentCalibration.GainDb;
            fftSize = _calibrationService.CurrentCalibration.FftSize;
            int filesPerPoint = ActivePlan.FilesPerPoint;
            int targetIndex = 0;
            calibrationBaselineSpectrum = _calibrationService.CurrentCalibration.BaselineSpectrum;

            var dwellSeconds = ActivePlan.DwellTime.TotalSeconds / filesPerPoint;
            var sampleRateHz = ActivePlan.SampleRate;
            var frequencyHz = ActivePlan.CenterFrequency;

            // Compute sample count safely
            uint sampleCount = (uint)Math.Ceiling(sampleRateHz * dwellSeconds);

            SpectrumVm.FftSize = fftSize;
            SpectrumVm.CenterFreqHz = frequencyHz;
            SpectrumVm.SamplingFrequencyHz = sampleRateHz;

            _chunkWorkerCts = new CancellationTokenSource();
            _ = Task.Run(() => ChunkWorker(_chunkWorkerCts.Token));

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




                // -----------------------------------------
                // NEW TARGET → reset the running spectrum
                // -----------------------------------------
                running = new double[fftSize];
                chunkCount = 0;
                SpectrumVm.UpdateSpectrum(running.ToArray());

                // Capture multiple files at this dwell point
                for (int i = 0; i < filesPerPoint; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // -----------------------------
                    // Stage 2 — Capturing
                    // -----------------------------
                    _statusBar.CaptureStatus = $"Capturing pos {targetIndex + 1} file {i + 1}";
                    StartProgressTimer(dwellSeconds);
                    captureInProgress = true;
                    

                    var rawIq = await _device.CaptureRawIqAsync(
                        frequencyHz,
                        sampleRateHz,
                        gainDb,
                        sampleCount,
                        ct);

                    captureInProgress = false;

                    StopProgressTimer();

                    string fullPath = FitsPathBuilder.BuildSweepFilePath(_optionsService.Options.CaptureFolder, "sweep", startTime, ActivePlan.CenterFrequency, target.ToString(), i + 1, filesPerPoint);

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

                targetIndex++;
            }
            _statusBar.CaptureStatus = "Completed";
        }
        catch (OperationCanceledException)
        {
            _statusBar.CaptureStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Error";
            MessageBox.Show(ex.Message, "Sweep Error");
        }
        finally
        {
            _chunkWorkerCts?.Cancel();
            _chunkWorkerCts = null;

            if (ActivePlan.TrackingEnabled && await _mount.GetCanSetTrackingAsync())
            {
                await _mount.SetTrackingAsync(false);
            }
            if (ActivePlan.GoToHomeAfterCapture)
            {
                if (await _mount.GetCanFindHomeAsync())
                {
                    await _mount.FindHomeAsync();
                }
            }
            captureInProgress = false;
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
