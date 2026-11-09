using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Infrastructure.Services;
using RASTA.Processing.Dsp;
using RASTA.Processing.HiPipeline;
using System.IO;
using System.Windows;

namespace RASTA.App.ViewModels;


public partial class VisualiseViewModel : ObservableObject
{
    private readonly StatusBarViewModel _statusBar;
    private readonly SpectrumCacheService _spectrumCache;
    private readonly CachedSpectrumLoader _spectrumLoader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFftSizeEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowDbEnabled))]
    [NotifyPropertyChangedFor(nameof(SmoothingEnabled))]
    [NotifyPropertyChangedFor(nameof(DespikeControlsEnabled))]
    private SpectrumMode mode = SpectrumMode.HiFrequency;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFftSizeEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowDbEnabled))]
    [NotifyPropertyChangedFor(nameof(SmoothingEnabled))]
    [NotifyPropertyChangedFor(nameof(DespikeControlsEnabled))]
    private string baselineFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFftSizeEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowDbEnabled))]
    [NotifyPropertyChangedFor(nameof(SmoothingEnabled))]
    [NotifyPropertyChangedFor(nameof(DespikeControlsEnabled))]
    private string captureFile;

    // Both files selected is what actually flips GenerateChartAsync over to the
    // Mode-driven combined pipelines (ProcessFilesHiFrequency/HiVelocity/ProcessSkaoTtrt/
    // ProcessFilesRatio) - with only one file selected it always falls back to the
    // single-file ProcessBaseline/ProcessCapture view regardless of Mode (see
    // GenerateChartAsync), where every one of the controls below applies. The four
    // properties below drive IsEnabled on VisualiseView.xaml's Target FFT Size/Show
    // dB/Smooth+Window/Despike+Sigma controls so a control with no effect in the
    // spectrum mode that's actually about to run is visibly disabled rather than
    // silently ignored - discovered the hard way when SKAO TTRT mode was selected with
    // only a Capture file chosen: the mode dropdown implied TTRT was running, but the
    // silent single-file fallback meant none of this pipeline's own behaviour (not
    // least the DC-spike excision) was ever exercised.
    private bool BothFilesSelected => BaselineFile is not null && CaptureFile is not null;

    /// <summary>
    /// Forced to 256 by ProcessSkaoTtrt whenever SKAO TTRT is the mode that will
    /// actually run (both files selected) - editing it has no effect there. Still
    /// relevant for HiFrequency/HiVelocity/Ratio, and for the single-file fallback
    /// (ProcessBaseline/ProcessCapture, which read it regardless of Mode).
    /// </summary>
    public bool TargetFftSizeEnabled => !(BothFilesSelected && Mode == SpectrumMode.TTRT);

    /// <summary>
    /// Only Ratio's RatioSpectrum stays strictly positive when both files are
    /// selected - HiFrequency/HiVelocity's continuum-subtracted HiSpectrum can go
    /// negative, and ProcessSkaoTtrt never applies UseDbScale at all. Always relevant
    /// for the single-file fallback (a plain power spectrum, always positive).
    /// </summary>
    public bool ShowDbEnabled => !BothFilesSelected || Mode == SpectrumMode.Ratio;

    /// <summary>
    /// SmoothingKind only ever reaches HiStreamingPipeline.Process's HiSpectrum output
    /// (HiFrequency/HiVelocity) - Ratio reads RatioSpectrum, computed before smoothing
    /// runs, and ProcessSkaoTtrt never smooths at all (the SKAO reference doesn't
    /// either). Always relevant for the single-file fallback, which smooths that
    /// file's own averaged spectrum directly.
    /// </summary>
    public bool SmoothingEnabled => !BothFilesSelected || Mode == SpectrumMode.HiFrequency || Mode == SpectrumMode.HiVelocity;

    /// <summary>
    /// DespikeEnabled/DespikeThresholdSigma feed HiStreamingPipeline.Despike, used by
    /// HiFrequency/HiVelocity/Ratio (all via ProcessHiCore) and the single-file
    /// fallback - never by ProcessSkaoTtrt, which excises its own DC/LO spike
    /// unconditionally instead (see SkaoHiPipelineProcessor.RemoveDcSpike) and has no
    /// use for this opt-in narrowband-RFI control.
    /// </summary>
    public bool DespikeControlsEnabled => !(BothFilesSelected && Mode == SpectrumMode.TTRT);

    private double[]? baselineSpectrum;

    private double[]? captureSpectrum;

    // Pre-despike copies of the arrays above, cached purely for
    // ExportDespikeDebugCsvCommand - lets a raw-vs-despiked diagnostic dump be produced
    // without recomputing anything, and without disturbing the despiked arrays used for
    // display.
    private double[]? rawBaselineSpectrum;
    private double[]? rawCaptureSpectrum;

    [ObservableProperty]
    private double[]? correctedSpectrum;

    [ObservableProperty]
    private int scanFftSize;

    // Mirrored into MosaicVm (see DespikeEnabled's remarks on the same pattern) - the intent
    // is "adjust it on Single Capture until it looks right, then Mosaic processes every file
    // with the same setting", not a second control to keep in sync by hand.
    [ObservableProperty]
    private int targetFftSize;

    partial void OnTargetFftSizeChanged(int value) => MosaicVm.TargetFftSize = value;

    [ObservableProperty]
    private string frameCount = string.Empty;

    // How many raw IQ files went into the currently displayed chart's capture data -
    // 1 unless the selected capture file matched the "..._{n}of{total}.fits" pattern
    // and sibling files were found alongside it (see LoadCaptureSpectra).
    [ObservableProperty]
    private int combinedFileCount = 1;

    // Set by ProcessHiCore whenever an LSR correction could be computed from the
    // capture file's recorded pointing/time/site, so the applied offset is visible
    // rather than silently baked into the velocity axis.
    [ObservableProperty]
    private string lsrInfo = string.Empty;


    [ObservableProperty]
    private double frequencyHz;

    [ObservableProperty]
    private double samplingHz;

    [ObservableProperty]
    private double gain;

    // Only meaningful for the standalone baseline/capture views (ProcessBaseline/
    // ProcessCapture) - those are plain power spectra, strictly positive, so dB is a
    // valid log transform. HiSpectrum (the combined HI-mode output) is continuum-
    // subtracted and can be negative/zero, so it can't be log-scaled the same way.
    [ObservableProperty]
    private bool useDbScale = true;

    // Opt-in narrowband-RFI excision (HiStreamingPipeline.Despike) - off by default,
    // matching the pipeline's own default. Applies to the standalone baseline/capture
    // views (ProcessBaseline/ProcessCapture) as well as the combined HiFrequency/
    // HiVelocity/Ratio modes via ProcessHiCore; not used by ProcessSkaoTtrt, which
    // excises its own DC/LO spike unconditionally instead (SkaoHiPipelineProcessor.
    // RemoveDcSpike - a deliberate divergence from the reference, unlike this opt-in
    // control - see CLAUDE.md) and has no use for a separate opt-in toggle. Mirrored
    // into MosaicVm so the Mosaic tab's processing follows this one toggle rather than
    // needing a second control of its own. See DespikeControlsEnabled for how this
    // reaches VisualiseView.xaml's IsEnabled binding.
    [ObservableProperty]
    private bool despikeEnabled;

    partial void OnDespikeEnabledChanged(bool value) => MosaicVm.DespikeEnabled = value;

    // How many local-noise standard deviations above the local median counts as a spike -
    // see HiStreamingPipeline.Despike. Exposed as a live control (rather than fixed at
    // HiConstants.DefaultDespikeThresholdSigma) because the right value depends on how
    // heavily-averaged a given spectrum is: a shorter capture dwell has a noisier local
    // floor than a long baseline dwell, so may need a lower threshold to catch the same
    // spikes - lower this if spikes are still visible with Despike ticked.
    [ObservableProperty]
    private double despikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma;

    partial void OnDespikeThresholdSigmaChanged(double value) => MosaicVm.DespikeThresholdSigma = value;

    // None by default, matching HiStreamingPipeline.Process's own default - the reference
    // pipeline never smooths its final output, and leaving raw per-bin scatter visible is
    // what let SpectrumViewModel.ApplyRobustYAxisRange reveal it in the first place. Only
    // affects HiSpectrum (via ProcessHiCore); RatioSpectrum and the SKAO TTRT cross-check
    // (SpectrumMode.TTRT, deliberately kept unmodified) are unaffected either way. Mirrored
    // into MosaicVm (see DespikeEnabled's remarks on the same pattern) - unlike HiSpectrum
    // here, MosaicProcessor.FindLinePeak applies it to the RatioSpectrum it actually searches
    // (see MosaicViewModel's own remarks), so it genuinely changes the Mosaic tab's values,
    // not just an unused stored field.
    [ObservableProperty]
    private SmoothingKind smoothingKind = SmoothingKind.None;

    partial void OnSmoothingKindChanged(SmoothingKind value) => MosaicVm.SmoothingKind = value;

    // A single FFT bin is far narrower than a real HI line, so the default window needs to
    // span many bins before smoothing visibly does anything - see the "very small change"
    // symptom that motivated making this configurable at all. User-tunable rather than fixed
    // so it can be dialed to whatever the line width in a given capture actually calls for.
    [ObservableProperty]
    private int smoothingWindow = 21;

    partial void OnSmoothingWindowChanged(int value) => MosaicVm.SmoothingWindow = value;

    // Own progress/busy state for GenerateChartAsync, deliberately separate from
    // StatusBarViewModel.CaptureProgress/IsCaptureInProgress - those are also driven by
    // CaptureViewModel (and Mosaic/Prepare), so a chart generated here while a capture
    // sweep is running elsewhere used to fight the same shared bar for ownership. Drives
    // the Cancel button that replaces "Generate Chart" while a chart is being generated
    // (see VisualiseView.xaml) - the button itself doubles as the progress indicator via
    // GenerationProgress, rather than a separate bar next to it.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateChartCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateChartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelGenerateChartCommand))]
    private bool isGenerating;

    [ObservableProperty]
    private double generationProgress;

    // Status text for the same flow - kept off StatusBarViewModel.CaptureStatus for the
    // same reason as GenerationProgress above: CaptureViewModel/MosaicViewModel/
    // PrepareViewModel write that same shared string, so a chart generated here while a
    // capture is running elsewhere used to stomp on (and be stomped on by) its status
    // text too. Surfaced as the Cancel button's own ToolTip (see VisualiseView.xaml)
    // rather than dropped, so the phase ("Processing baseline…" etc) is still visible.
    [ObservableProperty]
    private string generationStatus = string.Empty;

    // Only one GenerateChartAsync run at a time is ever in flight (GenerateChartCommand's
    // CanExecute enforces that), so a single field - rather than threading a
    // CancellationToken through every Process* call - is enough to let
    // LoadFileSpectra observe a cancellation request from CancelGenerateChart.
    private CancellationTokenSource? _generateCts;
    private CancellationToken _generateCt;

    // Where the chart currently on screen got its averaged spectra from (issue #19):
    // "Cache" (no raw IQ read at all), "Raw IQ" (everything recomputed) or
    // "Cache + raw IQ" (e.g. a cached baseline against a capture seen for the first
    // time). Set once a generation completes, from the hit/miss counts LoadFileSpectra
    // keeps for that run.
    [ObservableProperty]
    private string chartSource = string.Empty;

    private int _cacheHits;
    private int _cacheMisses;

    // Set only for the duration of RegenerateChartAsync - makes LoadFileSpectra ignore
    // (and then overwrite) whatever is cached for the selected files.
    private bool _forceRegenerate;

    public SpectrumViewModel SpectrumVm { get; private set; }

    // Backs the "Mosaic" tab - the folder-wide, multi-position counterpart to this
    // view model's single-file flow above. Owned here (rather than resolved separately
    // by VisualiseView) so VisualiseView can embed MosaicView as a second tab purely by
    // binding its DataContext to this property.
    public MosaicViewModel MosaicVm { get; }

    public VisualiseViewModel(StatusBarViewModel statusBar, MosaicViewModel mosaicVm, SpectrumCacheService spectrumCache, CachedSpectrumLoader spectrumLoader)
    {
        _statusBar = statusBar;
        _spectrumCache = spectrumCache;
        _spectrumLoader = spectrumLoader;
        MosaicVm = mosaicVm;
        // Keep Mosaic in sync with this view's despike/FFT-size/smoothing controls from the
        // start - the On...Changed partials below only fire on a later change, not on these
        // field initializers, so the very first values need setting explicitly here too.
        MosaicVm.DespikeEnabled = DespikeEnabled;
        MosaicVm.DespikeThresholdSigma = DespikeThresholdSigma;
        MosaicVm.TargetFftSize = TargetFftSize;
        MosaicVm.SmoothingKind = SmoothingKind;
        MosaicVm.SmoothingWindow = SmoothingWindow;
        SpectrumVm = new SpectrumViewModel(4096, 1420_405_800, 2.4e6); // default values; will be updated when calibration is loaded
    }

    // ---------------------------------------------------------
    // Progress reporting - real, measured progress (frames
    // processed / total frames), same pattern as Calibrator and
    // CaptureViewModel, not a time-based guess. Reported on this
    // view model's own GenerationProgress/GenerationStatus (see
    // fields above), not StatusBarViewModel's shared bar/text.
    // ---------------------------------------------------------

    private void BeginProgress(string status)
    {
        GenerationStatus = status;
        GenerationProgress = 0;
    }

    private void ReportProgress(double fraction)
    {
        GenerationProgress = Math.Clamp(fraction, 0.0, 1.0);
    }

    private void EndProgress()
    {
        GenerationProgress = 0;
    }

    /// <summary>
    /// Converts a strictly-positive linear power spectrum to dB (10*log10). Floors at a
    /// small epsilon first so an exact-zero bin produces a large negative number instead
    /// of -Infinity/NaN, which would otherwise break the chart's Y-axis autoscale.
    /// </summary>
    private static double[] ToDb(double[] linear)
    {
        const double epsilon = 1e-12;
        var db = new double[linear.Length];
        for (int i = 0; i < linear.Length; i++)
            db[i] = 10.0 * Math.Log10(Math.Max(linear[i], epsilon));
        return db;
    }

    /// <summary>
    /// If the given capture file's name matches the CaptureViewModel-generated
    /// "..._{index}of{total}.fits" pattern (multiple files captured at the same dwell
    /// point - see FitsPathBuilder.BuildSweepFilePath), returns every sibling file, in
    /// order, that actually exists alongside it in the same folder. Otherwise returns
    /// just the single selected file.
    /// </summary>
    private static List<string> ResolveRelatedCaptureFiles(string captureFilePath)
    {
        string dir = Path.GetDirectoryName(captureFilePath) ?? string.Empty;
        string fileNameNoExt = Path.GetFileNameWithoutExtension(captureFilePath);
        string ext = Path.GetExtension(captureFilePath);

        if (!FitsPathBuilder.TryParseSweepFileName(fileNameNoExt, out var basePart, out _, out var total))
            return new List<string> { captureFilePath };

        if (total <= 1)
            return new List<string> { captureFilePath };

        var related = new List<string>();
        for (int i = 1; i <= total; i++)
        {
            string candidate = Path.Combine(dir, $"{basePart}_{i}of{total}{ext}");
            if (File.Exists(candidate))
                related.Add(candidate);
        }

        // Fall back to just the selected file if, for some reason, nothing matched -
        // shouldn't happen since the selected file itself always matches its own name.
        return related.Count > 0 ? related : new List<string> { captureFilePath };
    }

    /// <summary>
    /// The native-resolution accumulated spectrum (plus header values) for one file set,
    /// from the cache when these exact files have been reduced before and from the raw IQ
    /// otherwise - see CachedSpectrumLoader, shared with MosaicProcessor, for what is cached
    /// and why no chart parameter is part of the key (issue #19). This wrapper only bridges
    /// its progress callback to GenerationStatus/GenerationProgress, passes the running
    /// generation's cancellation token and Regenerate flag, and counts hits/misses for
    /// ChartSource. A cancellation unwinds before the caller updates SpectrumVm, so the
    /// chart on screen keeps showing whatever was last successfully generated.
    /// </summary>
    private SpectrumCacheEntry LoadFileSpectra(IReadOnlyList<string> files, bool skao, string label)
    {
        var entry = _spectrumLoader.Load(
            files, skao, _forceRegenerate, label,
            (status, fraction) =>
            {
                GenerationStatus = status;
                ReportProgress(fraction);
            },
            _generateCt,
            out bool fromCache);

        if (fromCache)
            _cacheHits++;
        else
            _cacheMisses++;

        return entry;
    }

    private SpectrumCacheEntry LoadBaselineSpectra(bool skao = false) =>
        LoadFileSpectra(new[] { BaselineFile! }, skao, "baseline");

    /// <summary>
    /// Capture counterpart of LoadBaselineSpectra - also pulls in any "_{n}of{total}"
    /// sibling files (see ResolveRelatedCaptureFiles) and sets CombinedFileCount so the
    /// UI shows how many files went into the result.
    /// </summary>
    private SpectrumCacheEntry LoadCaptureSpectra(bool skao = false)
    {
        var files = ResolveRelatedCaptureFiles(CaptureFile!);
        CombinedFileCount = files.Count;
        return LoadFileSpectra(files, skao, "capture");
    }


    public bool BaselineAvailable => BaselineFile is not null;

    [RelayCommand]
    private void SelectBaselineFile()
    {
        SelectBaseline();
        OnPropertyChanged(nameof(BaselineAvailable));
    }

    [RelayCommand]
    private void ClearBaselineFile()
    {
        BaselineFile = null;
        OnPropertyChanged(nameof(BaselineAvailable));
    }

    public bool CaptureAvailable => CaptureFile is not null;


    [RelayCommand]
    private void SelectCaptureFile()
    {
        SelectCapture();
        OnPropertyChanged(nameof(CaptureAvailable));
    }

    [RelayCommand]
    private void ClearCaptureFile()
    {
        CaptureFile = null;
        OnPropertyChanged(nameof(CaptureAvailable));
    }


    private bool CanGenerateChart => !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanGenerateChart))]
    private async Task GenerateChartAsync()
    {
        if (BaselineFile is null && CaptureFile is null)
            return;

        _generateCts = new CancellationTokenSource();
        _generateCt = _generateCts.Token;
        IsGenerating = true;
        _cacheHits = 0;
        _cacheMisses = 0;

        BeginProgress("Processing…");

        try
        {
            // Run off the UI thread - these are synchronous CPU-bound loops with no
            // natural await points, so without this the UI thread never gets a chance
            // to repaint and the progress bar would just jump straight to 100%.
            await Task.Run(() =>
            {
                if (BaselineFile is not null && CaptureFile is not null)
                {
                    if (Mode == SpectrumMode.HiFrequency)
                        ProcessFilesHiFrequency();
                    else if (Mode == SpectrumMode.HiVelocity)
                        ProcessFilesHiVelocity();
                    else if (Mode == SpectrumMode.TTRT)
                        ProcessSkaoTtrt();
                    else if (Mode == SpectrumMode.Ratio)
                        ProcessFilesRatio();
                }
                else if (BaselineFile is not null)
                {
                    // Handle the case where only the baseline file is selected
                    ProcessBaseline();
                }
                else if (CaptureFile is not null)
                {
                    // Handle the case where only the capture file is selected
                    ProcessCapture();
                }
            }, _generateCt);

            GenerationStatus = "Completed";
            ChartSource = _cacheMisses == 0 ? "Cache"
                : _cacheHits == 0 ? "Raw IQ"
                : "Cache + raw IQ";
        }
        catch (OperationCanceledException)
        {
            GenerationStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            // Anything a Process* method throws (e.g. ValidateBaselineCaptureCompatibility's
            // sample-rate/FFT-size mismatch check) previously had nowhere to go - this method
            // had no general catch, so it surfaced as an unhandled exception on the UI thread
            // once Task.Run's await unwound, rather than a clear message telling the user why
            // nothing was generated.
            GenerationStatus = $"Failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Chart Generation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndProgress();
            IsGenerating = false;
            _generateCts?.Dispose();
            _generateCts = null;
        }
    }

    /// <summary>
    /// Generate Chart, but ignoring whatever SpectrumCacheService holds for the selected
    /// files - everything is re-read from the raw IQ and the cache entries overwritten.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateChart))]
    private async Task RegenerateChartAsync()
    {
        _forceRegenerate = true;
        try
        {
            await GenerateChartAsync();
        }
        finally
        {
            _forceRegenerate = false;
        }
    }

    [RelayCommand]
    private void ClearChartCache()
    {
        int removed = _spectrumCache.Clear();
        _statusBar.CaptureStatus = $"Cleared {removed} cached spectrum file(s).";
    }

    // Cancels a running GenerateChartAsync. CachedSpectrumLoader's per-frame
    // ThrowIfCancellationRequested() unwinds the current Process* method before it
    // updates SpectrumVm, so the chart on screen is left showing whatever was last
    // successfully generated rather than a half-updated one.
    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void CancelGenerateChart()
    {
        _generateCts?.Cancel();
        GenerationStatus = "Cancelling…";
    }

    private void SelectBaseline()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "FITS files (*.fits)|*.fits"
        };

        if (dlg.ShowDialog() == true)
            BaselineFile = dlg.FileName;
    }

    private void SelectCapture()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "FITS files (*.fits)|*.fits"
        };

        if (dlg.ShowDialog() == true)
            CaptureFile = dlg.FileName;

    }


    private void ProcessBaseline()
    {
        if (BaselineFile is null)
            return;
        CombinedFileCount = 1; // this view doesn't touch the capture file
        var baseline = LoadBaselineSpectra();
        ScanFftSize = baseline.FftSize;
        FrequencyHz = baseline.CentFreqHz;
        SamplingHz = baseline.SampFreqHz;
        Gain = baseline.GainDb;
        FrameCount = baseline.AveragedFrames.ToString();

        if (TargetFftSize == 0)
            TargetFftSize = ScanFftSize;   // default to no downscaling
        if (TargetFftSize > ScanFftSize)
            TargetFftSize = ScanFftSize;

        // ComputeSkAoPower deliberately leaves the spectrum in raw FFT-bin order (DC at
        // index 0) - shift it into monotonic frequency order before display, the same
        // way HiStreamingPipeline.Process does for the combined baseline/capture views.
        baselineSpectrum = HiStreamingPipeline.FftShift(baseline.AveragedPower!);
        if (TargetFftSize < ScanFftSize)
            baselineSpectrum = SpectrumBinner.BinAverage(baselineSpectrum, TargetFftSize);
        rawBaselineSpectrum = baselineSpectrum;
        if (DespikeEnabled)
            baselineSpectrum = HiStreamingPipeline.Despike(baselineSpectrum, SamplingHz, DespikeThresholdSigma);
        // Same smoothing kernel the combined HI views apply to their final output, but here
        // on the standalone averaged spectrum - lets the effect of Smooth/Window be judged
        // on this file alone, before it's ever divided against its counterpart.
        if (SmoothingKind != SmoothingKind.None)
            baselineSpectrum = HiStreamingPipeline.ApplySmoothing(baselineSpectrum, SmoothingKind, SmoothingWindow);

        SpectrumVm.Mode = SpectrumMode.HiFrequency;
        SpectrumVm.UpdateParameters(TargetFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(baselineSpectrum) : baselineSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
    }

    private void ProcessCapture()
    {
        if (CaptureFile is null)
            return;
        var capture = LoadCaptureSpectra();
        ScanFftSize = capture.FftSize;
        FrequencyHz = capture.CentFreqHz;
        SamplingHz = capture.SampFreqHz;
        Gain = capture.GainDb;
        FrameCount = capture.AveragedFrames.ToString();

        if (TargetFftSize == 0)
            TargetFftSize = ScanFftSize;   // default to no downscaling
        if (TargetFftSize > ScanFftSize)
            TargetFftSize = ScanFftSize;

        // Same fix as ProcessBaseline: shift out of raw FFT-bin order before display.
        captureSpectrum = HiStreamingPipeline.FftShift(capture.AveragedPower!);
        if (TargetFftSize < ScanFftSize)
            captureSpectrum = SpectrumBinner.BinAverage(captureSpectrum, TargetFftSize);
        rawCaptureSpectrum = captureSpectrum;
        if (DespikeEnabled)
            captureSpectrum = HiStreamingPipeline.Despike(captureSpectrum, SamplingHz, DespikeThresholdSigma);
        // Same smoothing kernel the combined HI views apply to their final output, but here
        // on the standalone averaged spectrum - lets the effect of Smooth/Window be judged
        // on this file alone, before it's ever divided against its counterpart.
        if (SmoothingKind != SmoothingKind.None)
            captureSpectrum = HiStreamingPipeline.ApplySmoothing(captureSpectrum, SmoothingKind, SmoothingWindow);

        SpectrumVm.Mode = SpectrumMode.HiFrequency;

        SpectrumVm.UpdateParameters(TargetFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(captureSpectrum) : captureSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
    }

    /// <summary>
    /// Diagnostic dump of the raw-vs-processed baseline/capture arrays cached by the last
    /// ProcessBaseline/ProcessCapture run (i.e. the standalone single-file views - run
    /// Generate Chart there first, with Baseline/Capture Only, not the combined
    /// HiFrequency/Ratio mode, since that goes through ProcessHiCore's pair-based despike
    /// instead and doesn't cache a "before" state). One row per FFT bin: bin index,
    /// frequency, and whichever of raw/processed baseline/capture were generated - the
    /// "processed" column reflects Despike and/or Smooth, whichever of those were ticked
    /// for that run (still labelled *Despiked for historical reasons; it's the same "after"
    /// column Despike originally added, now also carrying Smooth's effect when enabled).
    /// Written next to the source FITS file so it's easy to find, and readable directly off
    /// disk - no need to paste the contents anywhere. No CanExecute gating (unlike most
    /// commands here, which is deliberate - see e.g. ProcessBaseline/ProcessCapture's own
    /// early returns): this can run moments after a background Task.Run finishes populating
    /// the raw*Spectrum fields, and NotifyCanExecuteChanged from a background thread isn't
    /// safe to rely on here, so the guard is a plain early return instead.
    /// </summary>
    [RelayCommand]
    private void ExportDespikeDebugCsv()
    {
        double[]? reference = rawCaptureSpectrum ?? rawBaselineSpectrum;
        if (reference is null)
        {
            _statusBar.CaptureStatus = "Nothing to export yet - run Generate Chart first.";
            return;
        }

        string sourceFile = CaptureFile ?? BaselineFile!;
        string dir = Path.GetDirectoryName(sourceFile) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(sourceFile);
        string path = Path.Combine(dir, $"{baseName}_despike_debug.csv");

        int n = reference.Length;
        double df = SamplingHz / n;
        int mid = n / 2;

        using (var writer = new StreamWriter(path, append: false))
        {
            writer.WriteLine("Index,FrequencyHz,CaptureRaw,CaptureDespiked,BaselineRaw,BaselineDespiked");
            // Note: the *Despiked columns reflect Despike and/or Smooth, whichever were
            // enabled for the run that populated them - see the doc comment above.
            for (int i = 0; i < n; i++)
            {
                double freq = FrequencyHz + (i - mid) * df;
                string captureRaw = rawCaptureSpectrum is not null ? rawCaptureSpectrum[i].ToString("G6") : "";
                string captureDespiked = captureSpectrum is not null ? captureSpectrum[i].ToString("G6") : "";
                string baselineRaw = rawBaselineSpectrum is not null ? rawBaselineSpectrum[i].ToString("G6") : "";
                string baselineDespiked = baselineSpectrum is not null ? baselineSpectrum[i].ToString("G6") : "";
                writer.WriteLine($"{i},{freq:F1},{captureRaw},{captureDespiked},{baselineRaw},{baselineDespiked}");
            }
        }

        _statusBar.CaptureStatus = $"Exported despike debug CSV: {path}";
    }

    /// <summary>
    /// Baseline and capture must share the same sample rate and FFT size for any of
    /// the combined pipelines (ProcessHiCore/ProcessSkaoTtrt) to mean anything - a
    /// mismatch here isn't recoverable, so this runs immediately after the FITS
    /// metadata (or its cached copy) is available, before any reduction starts. Throws (naming the
    /// actual mismatched values, not just "do not match") rather than returning a
    /// bool, so a caller can't accidentally continue past a failure - GenerateChartAsync's
    /// own try/catch turns this into a MessageBox telling the user exactly why, instead
    /// of an unhandled exception on a background thread.
    /// </summary>
    private static void ValidateBaselineCaptureCompatibility(SpectrumCacheEntry baselineMeta, SpectrumCacheEntry captureMeta)
    {
        if (baselineMeta.SampFreqHz != captureMeta.SampFreqHz)
            throw new InvalidOperationException(
                $"Baseline and capture sample rates do not match ({baselineMeta.SampFreqHz:N0} Hz vs " +
                $"{captureMeta.SampFreqHz:N0} Hz) - they must be recorded at the same sample rate to be combined.");

        if (baselineMeta.FftSize != captureMeta.FftSize)
            throw new InvalidOperationException(
                $"Baseline and capture FFT sizes do not match ({baselineMeta.FftSize} vs {captureMeta.FftSize}) - " +
                "they must be recorded at the same FFT size to be combined.");
    }

    private (double[] baselineSpectrum, double[] captureSpectrum, HiStreamingPipeline hi)  ProcessHiCore()
    {
        if (BaselineFile is null || CaptureFile is null)
            return (Array.Empty<double>(), Array.Empty<double>(), new HiStreamingPipeline());

        // --- 1. Native-resolution averaged spectra: from cache, or from the raw IQ ---
        var baseline = LoadBaselineSpectra();
        var capture = LoadCaptureSpectra();

        // --- 2. Validate metadata ---
        ValidateBaselineCaptureCompatibility(baseline, capture);

        ScanFftSize = baseline.FftSize;
        FrequencyHz = baseline.CentFreqHz;
        SamplingHz = baseline.SampFreqHz;
        Gain = baseline.GainDb;

        if (TargetFftSize == 0)
            TargetFftSize = ScanFftSize;   // default to no downscaling
        if (TargetFftSize > ScanFftSize)
            TargetFftSize = ScanFftSize;

        FrameCount = $"{baseline.AveragedFrames}/{capture.AveragedFrames}";

        // --- 3. Re-bin down to Target FFT Size if requested - after averaging, via
        // SpectrumBinner, not by shrinking the raw IQ beforehand - see SpectrumBinner's
        // remarks for why that used to be a real bug (aliasing), not just a coarser
        // downscale.
        double[] baselineSpectrum = baseline.AveragedPower!;
        double[] captureSpectrum = capture.AveragedPower!;
        if (TargetFftSize < ScanFftSize)
        {
            baselineSpectrum = SpectrumBinner.BinAverage(baselineSpectrum, TargetFftSize);
            captureSpectrum = SpectrumBinner.BinAverage(captureSpectrum, TargetFftSize);
        }

        // --- 7. LSR correction, from the capture's recorded pointing/time/site (never the
        // baseline - even though it now has its own real, meaningful cold-sky pointing, it's
        // a calibration reference position, not the target actually being observed, so LSR
        // correction is about the capture's target, not the baseline's).
        double lsrCorrectionKmPerSec = capture.LsrCorrectionKmPerSec;
        LsrInfo = lsrCorrectionKmPerSec != 0.0
            ? $"{lsrCorrectionKmPerSec:+0.00;-0.00} km/s"
            : "n/a (no pointing/site recorded)";

        // --- 8. Run streaming HI pipeline ---
        var hi = new HiStreamingPipeline();
        hi.Process(
            baselineSpectrum,
            captureSpectrum,
            sampleRateHz: SamplingHz,
            centerFreqHz: FrequencyHz,
            lsrCorrectionKmPerSec: lsrCorrectionKmPerSec,
            despike: DespikeEnabled,
            despikeThresholdSigma: DespikeThresholdSigma,
            smoothing: SmoothingKind,
            smoothingWindow: SmoothingWindow
        );

        return (baselineSpectrum, captureSpectrum, hi);
    }

    private void ProcessFilesHiVelocity()
    {
        var (_, _, hi) = ProcessHiCore();

        SpectrumVm.Mode = SpectrumMode.HiVelocity;
        SpectrumVm.UpdateParameters(TargetFftSize, FrequencyHz, SamplingHz);

        SpectrumVm.UpdateSpectrum(hi.HiSpectrum, hi.VelocityKmPerSec);
    }

    private void ProcessFilesHiFrequency()
    {
        var (_, _, hi) = ProcessHiCore();

        SpectrumVm.Mode = SpectrumMode.HiFrequency;
        SpectrumVm.UpdateParameters(TargetFftSize, FrequencyHz, SamplingHz);

        SpectrumVm.UpdateSpectrum(hi.HiSpectrum, hi.FrequencyHz);
    }

    private void ProcessFilesRatio()
    {
        var (_, _, hi) = ProcessHiCore();

        // RatioSpectrum (capture/baseline, before continuum subtraction) is strictly
        // positive, so - unlike HiSpectrum - a dB view is valid here.
        SpectrumVm.Mode = SpectrumMode.Ratio;
        SpectrumVm.UpdateParameters(TargetFftSize, FrequencyHz, SamplingHz);

        var ratio = hi.RatioSpectrum;
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(ratio) : ratio, hi.FrequencyHz);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Ratio (dB)" : "Ratio";
    }

    private void ProcessSkaoTtrt()
    {
        if (BaselineFile is null || CaptureFile is null)
            return;

        // --- Per-file accumulated spectra: from cache, or from the raw IQ ---
        // The raw IQ is used at native resolution, unmodified.
        // SkaoHiObservation.AccumulateBinnedPower FFTs every whole native-size frame it
        // finds in each file (unwindowed, matching the reference exactly), bins each
        // frame's power spectrum down to SkaoConstants.NumIntegrationBins channels, and
        // sums that across every frame - the same "one full-resolution FFT, then sum
        // groups of adjacent bins, then sum across reads" shape
        // record_data.record_power_spectrum uses, just against however many native
        // ScanFftSize-length frames a RASTA capture actually contains rather than a fixed
        // count of 2**21-sample reads (RASTA doesn't capture in that read/reshape shape).
        // See SkaoHiObservation's own remarks for why an earlier version of this (many
        // small literal 256-point FFTs, averaged) didn't match the reference at all.
        var baseline = LoadBaselineSpectra(skao: true);
        var capture = LoadCaptureSpectra(skao: true);

        ValidateBaselineCaptureCompatibility(baseline, capture);

        ScanFftSize = baseline.FftSize;
        TargetFftSize = SkaoConstants.NumIntegrationBins;   // the reference's own fixed output bin count
        FrequencyHz = baseline.CentFreqHz;
        SamplingHz = baseline.SampFreqHz;
        Gain = baseline.GainDb;

        var skao = new SkaoHiObservation();
        skao.ProcessAccumulated(
            baseline.SkaoBinnedPower!,
            capture.SkaoBinnedPower!,
            baseline.SkaoFrames,
            capture.SkaoFrames,
            SamplingHz,
            FrequencyHz
        );

        FrameCount = $"{skao.BaselineFrameCount}/{skao.CaptureFrameCount}";
        ReportProgress(1.0);

        var hi = skao.Pipeline;

        // --- Update SpectrumViewModel ---
        SpectrumVm.Mode = SpectrumMode.HiFrequency;
        SpectrumVm.UpdateParameters(
            hi.HiSpectrum.Length,   // trimmed to 206 channels (256 - 2*ChCut) - see SkaoHiPipelineProcessor.Process
            FrequencyHz,
            SamplingHz
        );

        SpectrumVm.UpdateSpectrum(
            hi.HiSpectrum,
            hi.FrequencyHz
        );
    }

}
