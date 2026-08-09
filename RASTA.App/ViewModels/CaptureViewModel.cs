using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using RASTA.Core.Capture;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Services;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.Planning;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection.Metadata;
using System.Windows;

namespace RASTA.App.ViewModels;

public partial class CaptureViewModel : ObservableObject
{
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
    private readonly IPlanRepository _planRepository;
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
                OnPropertyChanged(nameof(CanCaptureSweep));
                OnPropertyChanged(nameof(CanDriftCapture));
            }
        }
    }

    public string PlanName => _activePlan?.FriendlyName ?? "No Plan";

    // Plans offered in the Capture dropdown - populated the same way
    // PlanViewModel.SavedPlans is (IPlanRepository.ListPlans for the connected SDR
    // device), then filtered to whichever PlanType matches the mount's current
    // CoordinateMode (see LoadAvailablePlans/PlanMatchesMountMode). Selection is no
    // longer pushed in from PlanViewModel.SelectedPlan on navigation.
    public ObservableCollection<CapturePlan> AvailablePlans { get; } = new();

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

    // Quick Capture - a single raw IQ grab at wherever the mount is currently pointed
    // (positioned by hand, or by a third-party ASCOM tool, rather than by a plan's
    // sweep). Frequency/sample rate/gain/FFT size all come from the active
    // CalibrationProfile - the same "must be used for all subsequent observations"
    // parameters CaptureSweepAsync draws gain/FFT size from - rather than from any
    // CapturePlan, so Quick Capture needs only a loaded calibration, not a selected plan.
    public bool CanQuickCapture => _device != null
        && _calibrationService.CurrentCalibration != null
        && _mountState.IsConnected
        && _sdrState.IsConnected
        && !IsBusy;

    [ObservableProperty]
    private double quickCaptureDwellSeconds = 30;

    [ObservableProperty]
    private bool isQuickCaptureRunning;

    [ObservableProperty]
    private bool isBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanQuickCapture));

    // Estimated wall-clock (local time) finish of the running sweep. Set from
    // SweepPlanResult's nominal dwell/slew estimate when the sweep starts, then
    // refined after every completed target using real measured per-point timing -
    // same "real, not simulated" progress philosophy as everything else in this
    // view model (see CaptureSweepAsync). Null when no sweep has run yet.
    [ObservableProperty]
    private DateTime? estimatedCompletionTime;

    public SpectrumViewModel SpectrumVm { get; private set; }

    public CaptureViewModel(
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
        IFftEngine fftEngine,
        IPlanRepository planRepository)
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
        _planRepository = planRepository;
        _device = sdrDeviceService.GetDevice();

        SpectrumVm = new SpectrumViewModel(4096, 1420_405_800, 2.4e6); // default values; will be updated when calibration is loaded

        _mountState.PropertyChanged += MountState_PropertyChanged;
        _sdrState.PropertyChanged += SdrState_PropertyChanged;
        if (_device is ISdrDevice sdrDevice)
        {
            sdrDevice.RawIqChunkAvailable += OnChunk;
        }

        LoadAvailablePlans();
    }

    /// <summary>
    /// Populates AvailablePlans the same way PlanViewModel.LoadSavedPlans does -
    /// IPlanRepository.ListPlans for the connected SDR device - then filters to plans
    /// whose PlanType matches the mount's current CoordinateMode (see
    /// PlanMatchesMountMode). Re-resolves the current selection against the freshly
    /// loaded instances (ListPlans deserializes new objects every call, so the old
    /// ActivePlan reference would otherwise match nothing in the reloaded list even if
    /// "the same" plan, by name, is still present), or clears it if no longer offered -
    /// e.g. the mount's coordinate mode changed since it was selected.
    /// </summary>
    public void LoadAvailablePlans()
    {
        string sdrDeviceId = _sdrState.SelectedDevice?.DeviceId ?? "UNKNOWN";
        string? previouslySelectedName = ActivePlan?.FriendlyName;

        AvailablePlans.Clear();
        foreach (var plan in _planRepository.ListPlans(sdrDeviceId))
        {
            if (PlanMatchesMountMode(plan))
                AvailablePlans.Add(plan);
        }

        ActivePlan = previouslySelectedName != null
            ? AvailablePlans.FirstOrDefault(p => p.FriendlyName == previouslySelectedName)
            : null;
    }

    /// <summary>
    /// A plan is only offered if its PlanType matches the connected mount's current
    /// CoordinateMode: Equatorial/AltAz plans need the mount actually in that mode to
    /// slew correctly, and Drift plans (a declination-based drift scan) only make sense
    /// under Equatorial tracking.
    /// </summary>
    private bool PlanMatchesMountMode(CapturePlan plan) => plan.PlanType switch
    {
        PlanType.AltAz => _mountState.Mode == CoordinateMode.AltAz,
        PlanType.Equatorial => _mountState.Mode == CoordinateMode.Equatorial,
        PlanType.Drift => _mountState.Mode == CoordinateMode.Equatorial,
        _ => false
    };

    private void MountState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TelescopeState.IsConnected))
        {
            OnPropertyChanged(nameof(CanCaptureSweep));
            OnPropertyChanged(nameof(CanDriftCapture));
            OnPropertyChanged(nameof(CanQuickCapture));
        }

        if (e.PropertyName is nameof(TelescopeState.IsConnected) or nameof(TelescopeState.Mode))
        {
            LoadAvailablePlans();
        }
    }

    private void SdrState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SdrState.IsConnected))
        {
            OnPropertyChanged(nameof(CanCaptureSweep));
            OnPropertyChanged(nameof(CanDriftCapture));
            OnPropertyChanged(nameof(CanQuickCapture));
        }

        if (e.PropertyName == nameof(SdrState.SelectedDevice))
        {
            LoadAvailablePlans();
        }
    }

    #region Live Spectrum Charting

    private readonly ConcurrentQueue<byte[]> _captureQueue = new();
    private readonly ConcurrentQueue<byte[]> _spectrumQueue = new();

    private CancellationTokenSource? _chunkWorkerCts;

    private int fftSize = 1024;

    private double[]? calibrationBaselineSpectrum;

    // Live HI pipeline state for the current dwell point. Reset per target so the
    // displayed spectrum builds up over that pointing's captures only.
    private HiStreamingAccumulator? _liveAccumulator;
    private readonly HiStreamingPipeline _livePipeline = new();
    private byte[] _liveLeftover = Array.Empty<byte>();
    private double _liveSampleRateHz;
    private double _liveCenterFreqHz;

    // Sourced from ActivePlan.DespikeEnabled for a sweep (see CaptureSweepAsync); Quick
    // Capture has no plan to read a setting from, so it stays off there (see
    // QuickCaptureAsync).
    private bool _liveDespikeEnabled;

    private void OnChunk(byte[] chunk)
    {
        // FAST: enqueue chunk for DSP worker
        _captureQueue.Enqueue(chunk);
        _spectrumQueue.Enqueue(chunk);
    }

    private async Task ChunkWorker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_spectrumQueue.TryDequeue(out var chunk))
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
        if (calibrationBaselineSpectrum == null || _liveAccumulator == null)
            return;

        // Raw streaming chunks arrive at whatever size the USB async buffer produced -
        // not aligned to fftSize. Stitch them onto any leftover from the previous chunk
        // and slice off every complete fftSize-aligned frame before computing power, so
        // frames line up the same way they do when chunking a full FITS capture.
        int bytesPerFrame = fftSize * 2;

        var combined = new byte[_liveLeftover.Length + chunk.Length];
        Buffer.BlockCopy(_liveLeftover, 0, combined, 0, _liveLeftover.Length);
        Buffer.BlockCopy(chunk, 0, combined, _liveLeftover.Length, chunk.Length);

        int usableFrames = combined.Length / bytesPerFrame;
        int usableBytes = usableFrames * bytesPerFrame;

        for (int f = 0; f < usableFrames; f++)
        {
            var frame = new byte[bytesPerFrame];
            Buffer.BlockCopy(combined, f * bytesPerFrame, frame, 0, bytesPerFrame);

            var power = _fftEngine.ComputeSkAoPower(frame, fftSize);
            _liveAccumulator.AddCaptureFrame(power);
        }

        int remainderLength = combined.Length - usableBytes;
        _liveLeftover = new byte[remainderLength];
        Buffer.BlockCopy(combined, usableBytes, _liveLeftover, 0, remainderLength);

        if (_liveAccumulator.CaptureFrames == 0)
            return; // not enough samples yet for even one aligned frame

        var captureAvg = _liveAccumulator.GetCaptureAverage();
        _livePipeline.Process(calibrationBaselineSpectrum, captureAvg, _liveSampleRateHz, _liveCenterFreqHz, despike: _liveDespikeEnabled);

        SpectrumVm.UpdateSpectrum(_livePipeline.HiSpectrum, _livePipeline.FrequencyHz);
    }

    private async Task<byte[]> CaptureRawIqFromStreamAsync(uint sampleCount, CancellationToken ct, Action<double>? onProgress = null)
    {
        ulong bytesNeeded = (ulong)sampleCount * 2UL;
        var output = new byte[bytesNeeded];
        ulong writePos = 0;

        while (writePos < bytesNeeded)
        {
            ct.ThrowIfCancellationRequested();

            if (_captureQueue.TryDequeue(out var chunk))
            {
                ulong toCopy = Math.Min((ulong)chunk.Length, bytesNeeded - writePos);
                Array.Copy(chunk, 0, output, (int)writePos, (int)toCopy);
                writePos += toCopy;

                // Real, measured progress - not a time-based guess.
                onProgress?.Invoke((double)writePos / bytesNeeded);
            }
            else
            {
                await Task.Delay(1, ct);
            }
        }

        return output;
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
        EstimatedCompletionTime = null; // clear any stale estimate from a previous run

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

        // Initial estimate from the plan's nominal dwell/slew figures - refined below
        // against real measured per-point timing as the sweep actually runs.
        EstimatedCompletionTime = sweepPlanResult.EstimatedCompletionUtc?.ToLocalTime();

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
            _liveDespikeEnabled = ActivePlan.DespikeEnabled;

            var dwellSeconds = ActivePlan.DwellTime.TotalSeconds / filesPerPoint;
            var sampleRateHz = ActivePlan.SampleRate;
            var frequencyHz = ActivePlan.CenterFrequency;

            // Compute sample count safely
            uint sampleCount = (uint)Math.Ceiling(sampleRateHz * dwellSeconds);

            System.Diagnostics.Debug.WriteLine($"CaptureSweepAsync: dwellSeconds={dwellSeconds}, sampleRateHz={sampleRateHz}, sampleCount={sampleCount}");

            // Prepare the spectrum view model with the correct parameters for the current plan.
            SpectrumVm.Mode = SpectrumMode.HiFrequency;
            SpectrumVm.UpdateParameters(fftSize, frequencyHz, sampleRateHz);

            _liveSampleRateHz = sampleRateHz;
            _liveCenterFreqHz = frequencyHz;

            // Marks when actual capturing began (after mount/device setup above), used
            // to measure real average time-per-point for refining EstimatedCompletionTime
            // as the sweep progresses.
            DateTime sweepExecutionStartUtc = DateTime.UtcNow;
            int totalTargetPoints = sweepPlanResult.Points.Count;

            foreach (var target in sweepPlanResult.Points)
            {
                // Fresh live accumulator for this pointing, so the displayed spectrum
                // builds up over this target's captures only, against the fixed
                // calibration baseline.
                _liveAccumulator = new HiStreamingAccumulator(fftSize);
                _liveLeftover = Array.Empty<byte>();

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

                SpectrumVm.UpdateSpectrum(new double[fftSize]);

                // Capture multiple files at this dwell point
                for (int i = 0; i < filesPerPoint; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // -----------------------------
                    // Stage 2 — Capturing
                    // -----------------------------
                    _statusBar.CaptureStatus = $"Capturing pos {targetIndex + 1} file {i + 1}";
                    var captureStartTime = DateTime.Now;

                    await _device.StartStreamingAsync(frequencyHz, sampleRateHz, gainDb, ct);

                    _chunkWorkerCts = new CancellationTokenSource();
                    _ = Task.Run(() => ChunkWorker(_chunkWorkerCts.Token));


                    BeginProgress();

                    var rawIq = await CaptureRawIqFromStreamAsync(sampleCount, ct, ReportProgress);

                    // -----------------------------
                    // Stage 2b — Stop streaming for this file
                    // -----------------------------
                    _chunkWorkerCts.Cancel();
                    _chunkWorkerCts = null;
                    await _device.StopStreamingAsync();
                    EndProgress();

                    System.Diagnostics.Debug.WriteLine($"CaptureSweepAsync: captured {rawIq.Length} bytes in {(DateTime.Now - captureStartTime).TotalSeconds:F2} seconds");


                    string fullPath = FitsPathBuilder.BuildSweepFilePath(_optionsService.Options.CaptureFolder, "sweep", startTime, ActivePlan.CenterFrequency, target.ToString(), i + 1, filesPerPoint);

                    var meta = new FitsFileMetaData
                    {
                        Origin = "RTL-SDR",
                        DataFormat = "UINT8_IQ",
                        CentFreqHz = frequencyHz,
                        SampFreqHz = sampleRateHz,
                        FftSize = fftSize, // Set t
                        GainDb = gainDb,
                        ObservationDate = DateTime.UtcNow,
                        DwellTimeSec = dwellSeconds,
                        SiteLatitudeDeg = _settings.SiteLatitudeDeg,
                        SiteLongitudeDeg = _settings.SiteLongitudeDeg,
                        SiteElevationM = _settings.SiteElevationM,
                        RaDeg = target.Mode == CoordinateMode.Equatorial ? target.RightAscensionHours * 15.0 : null,
                        DecDeg = target.Mode == CoordinateMode.Equatorial ? target.DeclinationDeg : null,
                        AzDeg = target.Mode == CoordinateMode.AltAz ? target.AzimuthDeg : null,
                        AltDeg = target.Mode == CoordinateMode.AltAz ? target.ElevationDeg : null
                    };

                    // -----------------------------
                    // Stage 3 — Saving
                    // -----------------------------
                    _statusBar.CaptureStatus = $"Saving pos {targetIndex + 1} file {i + 1}";
                    BeginProgress();

                    _fitsFileWriter.WriteRawIq(fullPath, rawIq, meta);

                    EndProgress();

                }

                // Refine the completion estimate from real, measured per-point timing
                // achieved so far, rather than the plan's nominal dwell/slew figures -
                // same "real, not simulated" progress convention used elsewhere.
                int pointsCompleted = targetIndex + 1;
                int pointsRemaining = totalTargetPoints - pointsCompleted;
                TimeSpan elapsed = DateTime.UtcNow - sweepExecutionStartUtc;
                TimeSpan avgPerPoint = TimeSpan.FromTicks(elapsed.Ticks / pointsCompleted);
                EstimatedCompletionTime = pointsRemaining > 0
                    ? (DateTime.UtcNow + TimeSpan.FromTicks(avgPerPoint.Ticks * pointsRemaining)).ToLocalTime()
                    : DateTime.Now;

                targetIndex++;
            }
            _statusBar.CaptureStatus = "Completed";
        }
        catch (OperationCanceledException)
        {
            _statusBar.CaptureStatus = "Cancelled";
            EstimatedCompletionTime = null; // no longer meaningful once the sweep won't finish
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Error";
            EstimatedCompletionTime = null;
            MessageBox.Show(ex.Message, "Sweep Error");
        }
        finally
        {
            _chunkWorkerCts?.Cancel();
            _chunkWorkerCts = null;
            await _device.StopStreamingAsync();

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
            IsBusy = false;
        }
    }

    // -------------------------------------------------------
    // Quick Capture - a single raw IQ file at the mount's current position, for use
    // when the mount has been positioned by hand or by a third-party ASCOM tool
    // instead of by a plan's sweep. Mirrors exactly what CaptureSweepAsync does for
    // one dwell point (same gain/FFT-size/frequency/sample-rate-from-the-active-
    // CalibrationProfile, same FitsFileMetaData shape) but skips slewing and plan/sweep
    // building entirely, substituting the mount's live TelescopeState reading for the
    // sweep's planned TargetPoint and QuickCaptureDwellSeconds for the plan's DwellTime.
    // -------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanQuickCapture))]
    private async Task QuickCaptureAsync()
    {
        if (_calibrationService.CurrentCalibration is null)
        {
            MessageBox.Show("No calibration profile loaded.");
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

        if (QuickCaptureDwellSeconds <= 0)
        {
            MessageBox.Show("Dwell period must be greater than zero.");
            return;
        }

        DateTime startTime = DateTime.UtcNow;

        // Capture wherever the mount currently is - only the coordinate pair the
        // connected mount's Mode reports directly (RA/Dec or Az/El) is recorded; the
        // other pair is reconstructed later from the stored site+time, exactly as
        // every other capture already does (see FitsFileMetaData / CLAUDE.md).
        var currentTarget = _mountState.Mode == CoordinateMode.Equatorial
            ? TargetPoint.FromRaDec(_mountState.RightAscensionHours, _mountState.DeclinationDeg)
            : TargetPoint.FromAzEl(_mountState.AzimuthDeg, _mountState.ElevationDeg);

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            IsBusy = true;
            IsQuickCaptureRunning = true;

            double gainDb = _calibrationService.CurrentCalibration.GainDb;
            fftSize = _calibrationService.CurrentCalibration.FftSize;
            calibrationBaselineSpectrum = _calibrationService.CurrentCalibration.BaselineSpectrum;
            // No CapturePlan behind Quick Capture (see CanQuickCapture), so there's no
            // per-plan DespikeEnabled to read - leave the live view undespiked.
            _liveDespikeEnabled = false;

            var dwellSeconds = QuickCaptureDwellSeconds;
            var sampleRateHz = _calibrationService.CurrentCalibration.SampleRateHz;
            var frequencyHz = _calibrationService.CurrentCalibration.CenterFrequencyHz;

            uint sampleCount = (uint)Math.Ceiling(sampleRateHz * dwellSeconds);

            SpectrumVm.Mode = SpectrumMode.HiFrequency;
            SpectrumVm.UpdateParameters(fftSize, frequencyHz, sampleRateHz);

            _liveSampleRateHz = sampleRateHz;
            _liveCenterFreqHz = frequencyHz;

            _liveAccumulator = new HiStreamingAccumulator(fftSize);
            _liveLeftover = Array.Empty<byte>();
            SpectrumVm.UpdateSpectrum(new double[fftSize]);

            // -----------------------------
            // Capturing
            // -----------------------------
            _statusBar.CaptureStatus = "Quick capture: capturing";

            await _device.StartStreamingAsync(frequencyHz, sampleRateHz, gainDb, ct);

            _chunkWorkerCts = new CancellationTokenSource();
            _ = Task.Run(() => ChunkWorker(_chunkWorkerCts.Token));

            BeginProgress();
            var rawIq = await CaptureRawIqFromStreamAsync(sampleCount, ct, ReportProgress);

            _chunkWorkerCts.Cancel();
            _chunkWorkerCts = null;
            await _device.StopStreamingAsync();
            EndProgress();

            string fullPath = FitsPathBuilder.BuildSweepFilePath(_optionsService.Options.CaptureFolder, "quick", startTime, frequencyHz, currentTarget.ToString(), 1, 1);

            var meta = new FitsFileMetaData
            {
                Origin = "RTL-SDR",
                DataFormat = "UINT8_IQ",
                CentFreqHz = frequencyHz,
                SampFreqHz = sampleRateHz,
                FftSize = fftSize,
                GainDb = gainDb,
                ObservationDate = DateTime.UtcNow,
                DwellTimeSec = dwellSeconds,
                SiteLatitudeDeg = _settings.SiteLatitudeDeg,
                SiteLongitudeDeg = _settings.SiteLongitudeDeg,
                SiteElevationM = _settings.SiteElevationM,
                RaDeg = currentTarget.Mode == CoordinateMode.Equatorial ? currentTarget.RightAscensionHours * 15.0 : null,
                DecDeg = currentTarget.Mode == CoordinateMode.Equatorial ? currentTarget.DeclinationDeg : null,
                AzDeg = currentTarget.Mode == CoordinateMode.AltAz ? currentTarget.AzimuthDeg : null,
                AltDeg = currentTarget.Mode == CoordinateMode.AltAz ? currentTarget.ElevationDeg : null
            };

            // -----------------------------
            // Saving
            // -----------------------------
            _statusBar.CaptureStatus = "Quick capture: saving";
            BeginProgress();

            _fitsFileWriter.WriteRawIq(fullPath, rawIq, meta);

            EndProgress();

            _statusBar.CaptureStatus = "Quick capture complete";
        }
        catch (OperationCanceledException)
        {
            _statusBar.CaptureStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Error";
            MessageBox.Show(ex.Message, "Quick Capture Error");
        }
        finally
        {
            _chunkWorkerCts?.Cancel();
            _chunkWorkerCts = null;
            await _device.StopStreamingAsync();
            IsBusy = false;
            IsQuickCaptureRunning = false;
        }
    }

    private void BeginProgress()
    {
        _statusBar.CaptureProgress = 0;
        _statusBar.IsCaptureInProgress = true;
    }

    private void ReportProgress(double fraction)
    {
        _statusBar.CaptureProgress = Math.Clamp(fraction, 0.0, 1.0);
    }

    private void EndProgress()
    {
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
            IsBusy = true;
            if (target.Mode == CoordinateMode.Equatorial)
                await _mount.SlewToRaDecAsync(target.RightAscensionHours, target.DeclinationDeg);
            else
                await _mount.SlewToAzAltAsync(target.AzimuthDeg, target.ElevationDeg);
        }
        finally
        {
            IsBusy = false;
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
            IsBusy = true;
            await _mount.SlewToAzAltAsync(azDeg, altDeg);
        }
        finally
        {
            IsBusy = false;
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
            IsBusy = true;
            await _mount.SlewToRaDecAsync(raHours, decDeg);
        }
        finally
        {
            IsBusy = false;
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
            IsBusy = true;
            await _mount.AbortSlewAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------
    // Capture observation
    // -------------------------------------------------------


}
