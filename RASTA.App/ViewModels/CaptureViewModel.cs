using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Helpers;
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
    private CancellationTokenSource? _quickCaptureCts;

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

    // Plans offered in the Capture dropdown - populated the same way PlanViewModel.SavedPlans
    // is (IPlanRepository.ListPlans - plans are no longer tied to a specific SDR device), then
    // filtered to whichever PlanType matches the mount's current CoordinateMode (see
    // LoadAvailablePlans/PlanMatchesMountMode). Selection is no longer pushed in from
    // PlanViewModel.SelectedPlan on navigation.
    public ObservableCollection<CapturePlan> AvailablePlans { get; } = new();

    public bool CanCaptureSweep => _activePlan != null
        && _device != null
        && _calibrationService.IsCalibrationAvailable
        && _mountState.IsConnected
        && _sdrState.IsConnected
        && _activePlan.PlanType != PlanType.Drift
        && !IsBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelSweepCommand))]
    private bool isSweepCaptureRunning;


    public bool CanDriftCapture => _activePlan != null
        && _device != null
        && _calibrationService.IsCalibrationAvailable
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
        && _calibrationService.IsCalibrationAvailable
        && _mountState.IsConnected
        && _sdrState.IsConnected
        && !IsBusy;

    [ObservableProperty]
    private double quickCaptureDwellSeconds = 30;

    // Set by PlanViewModel.CaptureHereCommand (the sky map's right-click "Slew & Capture Here")
    // to hand off a specific target ahead of navigating here, instead of QuickCaptureAsync's
    // usual "wherever the mount currently is" behaviour. QuickCaptureAsync slews here first when
    // this is set, then clears it - so the *next* Quick Capture, with no hand-off, reverts to
    // "wherever pointed" automatically.
    [ObservableProperty]
    private TargetPoint? pendingQuickCaptureTarget;

    public string QuickCaptureTargetLabel => PendingQuickCaptureTarget is { } t
        ? $"Target: {t} (from Plan map)"
        : "Target: current mount position";

    partial void OnPendingQuickCaptureTargetChanged(TargetPoint? value) =>
        OnPropertyChanged(nameof(QuickCaptureTargetLabel));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelQuickCaptureCommand))]
    private bool isQuickCaptureRunning;

    [ObservableProperty]
    private bool isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanQuickCapture));
        OnPropertyChanged(nameof(CanCaptureSweep));
    }

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
    /// IPlanRepository.ListPlans (plans are no longer tied to a specific SDR device) - then
    /// filters to plans whose PlanType matches the mount's current CoordinateMode (see
    /// PlanMatchesMountMode). Re-resolves the current selection against the freshly
    /// loaded instances (ListPlans deserializes new objects every call, so the old
    /// ActivePlan reference would otherwise match nothing in the reloaded list even if
    /// "the same" plan, by name, is still present), or clears it if no longer offered -
    /// e.g. the mount's coordinate mode changed since it was selected.
    /// </summary>
    public void LoadAvailablePlans()
    {
        // MountState_PropertyChanged can invoke this from a background thread -
        // TelescopeService's poll loop runs inside its own Task.Run and mutates
        // TelescopeState from there, which raises this PropertyChanged event synchronously on
        // that same thread. Unlike a plain property notification (which WPF's binding
        // machinery marshals to the UI thread automatically), AvailablePlans.Clear()/
        // Add() below are ObservableCollection mutations bound directly to the UI and
        // must happen on the dispatcher thread itself, or WPF throws. UiThread.
        // SafeInvoke is a no-op if called from the UI thread already (constructor/
        // NavigationViewModel call it directly), so this is safe from any caller.
        UiThread.SafeInvoke(() =>
        {
            string? previouslySelectedName = ActivePlan?.FriendlyName;

            AvailablePlans.Clear();
            foreach (var plan in _planRepository.ListPlans())
            {
                if (PlanMatchesMountMode(plan))
                    AvailablePlans.Add(plan);
            }

            ActivePlan = previouslySelectedName != null
                ? AvailablePlans.FirstOrDefault(p => p.FriendlyName == previouslySelectedName)
                : null;
        });
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

        // Non-fatal: some points at the tail of the sweep never clear the horizon limit for
        // their own dwell and were dropped rather than cancelling the whole sweep (see
        // SweepPlanner.BuildSweepFromPoints) - let the user know before it actually runs.
        if (sweepPlanResult.Warning != null)
        {
            MessageBox.Show(sweepPlanResult.Warning, "Sweep plan warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Initial estimate from the plan's nominal dwell/slew figures - refined below
        // against real measured per-point timing as the sweep actually runs.
        EstimatedCompletionTime = sweepPlanResult.EstimatedCompletionUtc?.ToLocalTime();

        // Snapshot the mount's own tracking/at-home state before this sweep touches
        // either, so both can be restored exactly as found once the sweep finishes or is
        // cancelled (see the finally block below). Queried once up front rather than
        // repeatedly, since a mount that can't report Can*/Get* shouldn't be asked again
        // per target point.
        bool canSetTracking = await _mount.GetCanSetTrackingAsync();
        bool originalTrackingEnabled = canSetTracking && await _mount.GetTrackingAsync();
        bool canFindHome = await _mount.GetCanFindHomeAsync();
        bool wasAtHomeAtStart = canFindHome && await _mount.GetAtHomeAsync();

        // This mount's ASCOM driver rejects a slew outright unless tracking is already
        // on, regardless of whether the plan itself wants tracking left on through the
        // dwell - so tracking always goes on here. If Tracking Enabled is ticked on the
        // plan it simply stays on for the sweep's duration (the per-point loop below
        // never turns it off); otherwise it's dropped back to originalTrackingEnabled
        // right after each point's slew completes, and restored one final time in the
        // finally block regardless of how the sweep ends.
        if (canSetTracking)
        {
            await _mount.SetTrackingAsync(true);
        }

        _sweepCts = new CancellationTokenSource();
        var ct = _sweepCts.Token;
        try
        {
            IsBusy = true;
            IsSweepCaptureRunning = true;

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

                // If Tracking Enabled isn't ticked, tracking was dropped back to
                // originalTrackingEnabled after the previous point's slew (below) - turn
                // it back on now so this slew isn't rejected by the mount. A no-op when
                // Tracking Enabled is ticked, since it's already on for the whole sweep.
                if (canSetTracking && !ActivePlan.TrackingEnabled)
                {
                    await _mount.SetTrackingAsync(true);
                }

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
                catch (OperationCanceledException)
                {
                    // A real user cancellation (ct itself) should propagate up to the
                    // outer catch (OperationCanceledException) below rather than being
                    // swallowed here. Deliberately not an exception filter
                    // (`when (!ct.IsCancellationRequested)`) - a filtered catch on one
                    // throw in this async method's compiled state machine can cause the
                    // debugger to mis-flag an unrelated, genuinely-handled throw
                    // elsewhere in the same method (e.g. ct.ThrowIfCancellationRequested()
                    // below) as user-unhandled during first-chance dispatch.
                    if (ct.IsCancellationRequested)
                        throw;

                    // ct was not cancelled — this was our timeout firing
                    MessageBox.Show("Slew timed out after 30 seconds. Capture will be abandoned.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // The slew is done - if the plan doesn't want tracking on for the dwell,
                // drop it back to whatever the mount was set to before this sweep began.
                if (canSetTracking && !ActivePlan.TrackingEnabled)
                {
                    await _mount.SetTrackingAsync(originalTrackingEnabled);
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

            // Best-effort only: if the mount itself is what triggered this cancellation
            // (see CancelAnyRunningCapture / TelescopeService.ConnectionLost), these live
            // mount calls will throw too. Swallowing that here matters - an exception
            // escaping a finally block skips whatever's left in it, which would otherwise
            // leave IsSweepCaptureRunning/IsBusy stuck true and the Cancel Sweep button
            // stuck visible even after the sweep has genuinely stopped.
            try
            {
                // Return home if the plan explicitly asks for it, or if the mount was
                // already at home before this sweep started - restoring that starting
                // state even when GoToHomeAfterCapture isn't ticked.
                bool shouldReturnHome = ActivePlan.GoToHomeAfterCapture || wasAtHomeAtStart;

                if (shouldReturnHome && canFindHome)
                {
                    // This mount refuses a slew (FindHome included) while tracking is
                    // active, regardless of what originalTrackingEnabled will restore it
                    // to below - so tracking must come off first if it's currently on.
                    if (canSetTracking && await _mount.GetTrackingAsync())
                    {
                        await _mount.SetTrackingAsync(false);
                    }
                    await _mount.FindHomeAsync();
                }

                // Always put tracking back exactly how the mount had it before this sweep
                // began, whether the sweep completed, failed, or was cancelled.
                if (canSetTracking)
                {
                    await _mount.SetTrackingAsync(originalTrackingEnabled);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CaptureSweepAsync: post-sweep mount cleanup failed: {ex.Message}");
            }

            _sweepCts?.Dispose();
            _sweepCts = null;
            IsSweepCaptureRunning = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancels whichever capture (sweep or Quick Capture) is currently running, if any.
    /// Used by both the user's own Cancel Sweep/Cancel Quick Capture buttons' underlying
    /// tokens and by App.xaml.cs's mount-disconnect recovery path (TelescopeService.
    /// ConnectionLost), which needs any in-flight capture aborted - and its FITS write
    /// skipped, same as a manual cancel - before the mount is reset out from under it.
    /// Cancelling an already-idle/null token is a harmless no-op.
    /// </summary>
    public void CancelAnyRunningCapture()
    {
        _sweepCts?.Cancel();
        _quickCaptureCts?.Cancel();
    }

    // Cancels a running sweep (Begin Sweep). The capture loop's own
    // ct.ThrowIfCancellationRequested()/CaptureRawIqFromStreamAsync unwind before
    // reaching FitsFileIo.WriteRawIq for whichever file was in flight, so the
    // in-progress capture's FITS file is never written to disk - only prior,
    // already-completed files in the sweep remain.
    [RelayCommand(CanExecute = nameof(IsSweepCaptureRunning))]
    private void CancelSweep()
    {
        _sweepCts?.Cancel();
        _statusBar.CaptureStatus = "Cancelling...";
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

        TargetPoint currentTarget;
        if (PendingQuickCaptureTarget is { } pendingTarget)
        {
            // Hand-off from PlanViewModel's sky map ("Slew & Capture Here") - slew there first,
            // then use that target directly rather than re-reading _mountState (which is only
            // refreshed by TelescopeService's poll loop, so reading it immediately after a slew
            // could still race and see stale position data).
            _statusBar.CaptureStatus = "Quick capture: slewing to target";
            if (!await SlewToPendingTargetAsync(pendingTarget))
                return; // error already shown inside the helper

            currentTarget = pendingTarget;
            PendingQuickCaptureTarget = null; // one-shot - the next Quick Capture reverts to "wherever pointed"
        }
        else
        {
            // Capture wherever the mount currently is - only the coordinate pair the
            // connected mount's Mode reports directly (RA/Dec or Az/El) is recorded; the
            // other pair is reconstructed later from the stored site+time, exactly as
            // every other capture already does (see FitsFileMetaData / CLAUDE.md).
            currentTarget = _mountState.Mode == CoordinateMode.Equatorial
                ? TargetPoint.FromRaDec(_mountState.RightAscensionHours, _mountState.DeclinationDeg)
                : TargetPoint.FromAzEl(_mountState.AzimuthDeg, _mountState.ElevationDeg);
        }

        _quickCaptureCts = new CancellationTokenSource();
        var ct = _quickCaptureCts.Token;
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
            _quickCaptureCts?.Dispose();
            _quickCaptureCts = null;
            IsBusy = false;
            IsQuickCaptureRunning = false;
        }
    }

    // Cancels a running Quick Capture. As with CancelSweep, cancellation unwinds
    // CaptureRawIqFromStreamAsync before FitsFileIo.WriteRawIq is ever reached, so
    // nothing is written to disk for the aborted capture.
    [RelayCommand(CanExecute = nameof(IsQuickCaptureRunning))]
    private void CancelQuickCapture()
    {
        _quickCaptureCts?.Cancel();
        _statusBar.CaptureStatus = "Cancelling quick capture...";
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
    /// <summary>
    /// Slews to a PlanViewModel-supplied ad-hoc target ahead of a Quick Capture. This mount's
    /// ASCOM driver rejects a slew outright unless tracking is already on (same reasoning
    /// CaptureSweepAsync documents for its own per-point slews), so tracking is switched on
    /// first if needed and left on afterward - this is a one-off manual "go look at this spot"
    /// action, not a sweep with restore-to-original-state semantics.
    /// </summary>
    private async Task<bool> SlewToPendingTargetAsync(TargetPoint target)
    {
        try
        {
            bool canSetTracking = await _mount.GetCanSetTrackingAsync();
            if (canSetTracking && !await _mount.GetTrackingAsync())
                await _mount.SetTrackingAsync(true);

            if (target.Mode == CoordinateMode.Equatorial)
                await _mount.SlewToRaDecAsync(target.RightAscensionHours, target.DeclinationDeg);
            else
                await _mount.SlewToAzAltAsync(target.AzimuthDeg, target.ElevationDeg);

            return true;
        }
        catch (Exception ex)
        {
            _statusBar.CaptureStatus = "Error";
            MessageBox.Show(ex.Message, "Slew Error");
            return false;
        }
    }

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
