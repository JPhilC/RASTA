using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Processing.Dsp;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.HiPipeline.RASTA.Processing.HiPipeline;
using System.IO;

namespace RASTA.App.ViewModels;


public partial class VisualiseViewModel : ObservableObject
{
    private readonly FitsFileIo _fitsFileIo;
    private readonly IFftEngine _fftEngine;
    private readonly StatusBarViewModel _statusBar;

    [ObservableProperty]
    private SpectrumMode mode = SpectrumMode.HiFrequency;


    [ObservableProperty]
    private string baselineFile;

    [ObservableProperty]
    private string captureFile;

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

    [ObservableProperty]
    private int targetFftSize;

    [ObservableProperty]
    private string frameCount = string.Empty;

    // How many raw IQ files went into the currently displayed chart's capture data -
    // 1 unless the selected capture file matched the "..._{n}of{total}.fits" pattern
    // and sibling files were found alongside it (see ReadCombinedCaptureRawIq).
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
    // HiVelocity/Ratio modes via ProcessHiCore; deliberately left out of ProcessSkaoTtrt,
    // which stays an unmodified cross-check against the SKAO reference algorithm (same
    // reasoning RemoveDcSpike already follows). Mirrored into MosaicVm so the Mosaic tab's
    // processing follows this one toggle rather than needing a second control of its own.
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
    // (SpectrumMode.TTRT, deliberately kept unmodified) are unaffected either way.
    [ObservableProperty]
    private SmoothingKind smoothingKind = SmoothingKind.None;

    // A single FFT bin is far narrower than a real HI line, so the default window needs to
    // span many bins before smoothing visibly does anything - see the "very small change"
    // symptom that motivated making this configurable at all. User-tunable rather than fixed
    // so it can be dialed to whatever the line width in a given capture actually calls for.
    [ObservableProperty]
    private int smoothingWindow = 21;

    // Own progress/busy state for GenerateChartAsync, deliberately separate from
    // StatusBarViewModel.CaptureProgress/IsCaptureInProgress - those are also driven by
    // CaptureViewModel (and Mosaic/Prepare), so a chart generated here while a capture
    // sweep is running elsewhere used to fight the same shared bar for ownership. Drives
    // the Cancel button that replaces "Generate Chart" while a chart is being generated
    // (see VisualiseView.xaml) - the button itself doubles as the progress indicator via
    // GenerationProgress, rather than a separate bar next to it.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateChartCommand))]
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
    // CancellationToken through every Process*/ForEachChunk call - is enough to let
    // ForEachChunk observe a cancellation request from CancelGenerateChart.
    private CancellationTokenSource? _generateCts;
    private CancellationToken _generateCt;

    public SpectrumViewModel SpectrumVm { get; private set; }

    // Backs the "Mosaic" tab - the folder-wide, multi-position counterpart to this
    // view model's single-file flow above. Owned here (rather than resolved separately
    // by VisualiseView) so VisualiseView can embed MosaicView as a second tab purely by
    // binding its DataContext to this property.
    public MosaicViewModel MosaicVm { get; }

    public VisualiseViewModel(FitsFileIo fits, IFftEngine fftEngine, StatusBarViewModel statusBar, MosaicViewModel mosaicVm)
    {
        _fitsFileIo = fits;
        _fftEngine = fftEngine;
        _statusBar = statusBar;
        MosaicVm = mosaicVm;
        // Keep Mosaic in sync with this view's despike controls from the start.
        MosaicVm.DespikeEnabled = DespikeEnabled;
        MosaicVm.DespikeThresholdSigma = DespikeThresholdSigma;
        SpectrumVm = new SpectrumViewModel(4096, 1420_405_800, 2.4e6); // default values; will be updated when calibration is loaded
    }

    // ---------------------------------------------------------
    // Progress reporting - real, measured progress (chunks
    // processed / total chunks), same pattern as Calibrator and
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
    /// Iterates fixed-size chunks of raw IQ, invoking processChunk on each and reporting
    /// real, measured progress (chunks processed / total chunks) as it goes. Checks
    /// _generateCt each iteration so CancelGenerateChart can unwind the loop promptly
    /// rather than only between whole-file phases.
    /// </summary>
    private void ForEachChunk(byte[] iq, int bytesPerChunk, Action<byte[]> processChunk)
    {
        int totalChunks = bytesPerChunk > 0 ? iq.Length / bytesPerChunk : 0;
        int processedChunks = 0;

        for (int offset = 0; offset + bytesPerChunk <= iq.Length; offset += bytesPerChunk)
        {
            _generateCt.ThrowIfCancellationRequested();

            var chunk = new byte[bytesPerChunk];
            Buffer.BlockCopy(iq, offset, chunk, 0, bytesPerChunk);

            processChunk(chunk);

            processedChunks++;
            if (totalChunks > 0)
                ReportProgress((double)processedChunks / totalChunks);
        }
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
    /// Reads a capture file plus any related "_{n}of{total}" sibling files found
    /// alongside it (see ResolveRelatedCaptureFiles) and concatenates their raw IQ
    /// into one buffer, reporting per-file read progress as it goes. Sets
    /// CombinedFileCount so the UI shows how many files went into the result. The
    /// actual read/validate/trim/concatenate work lives in FitsFileIo.ReadCombinedRawIq
    /// (shared with MosaicViewModel's whole-folder flow) - this just resolves which
    /// files belong together and bridges its progress callback to GenerationStatus.
    /// </summary>
    private (FitsFileMetaData meta, byte[] iq) ReadCombinedCaptureRawIq(string captureFilePath)
    {
        var files = ResolveRelatedCaptureFiles(captureFilePath);
        CombinedFileCount = files.Count;

        return _fitsFileIo.ReadCombinedRawIq(files, (status, fraction) =>
        {
            GenerationStatus = status;
            ReportProgress(fraction);
        });
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
        }
        catch (OperationCanceledException)
        {
            GenerationStatus = "Cancelled.";
        }
        finally
        {
            EndProgress();
            IsGenerating = false;
            _generateCts?.Dispose();
            _generateCts = null;
        }
    }

    // Cancels a running GenerateChartAsync. ForEachChunk's per-iteration
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
        var (baselineMeta, baselineIq) = _fitsFileIo.ReadRawIq(BaselineFile);
        ScanFftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;

        // Average the same way HiStreamingPipeline/Calibrator do: SKAO-normalized power
        // per fftSize frame, arithmetic mean via HiStreamingAccumulator.
        var acc = new HiStreamingAccumulator(ScanFftSize);
        int bytesPerChunk = ScanFftSize * 2; // adjust if your IQ format differs

        BeginProgress("Processing baseline…");
        ForEachChunk(baselineIq, bytesPerChunk, chunk =>
        {
            var spectrum = _fftEngine.ComputeSkAoPower(chunk, ScanFftSize);
            acc.AddBaselineFrame(spectrum);
        });

        // ComputeSkAoPower deliberately leaves the spectrum in raw FFT-bin order (DC at
        // index 0) - shift it into monotonic frequency order before display, the same
        // way HiStreamingPipeline.Process does for the combined baseline/capture views.
        baselineSpectrum = HiStreamingPipeline.FftShift(acc.GetBaselineAverage());
        rawBaselineSpectrum = baselineSpectrum;
        if (DespikeEnabled)
            baselineSpectrum = HiStreamingPipeline.Despike(baselineSpectrum, SamplingHz, DespikeThresholdSigma);

        SpectrumVm.Mode = SpectrumMode.HiFrequency;
        SpectrumVm.UpdateParameters(ScanFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(baselineSpectrum) : baselineSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
    }

    private void ProcessCapture()
    {
        if (CaptureFile is null)
            return;
        var (captureMeta, captureIq) = ReadCombinedCaptureRawIq(CaptureFile);
        ScanFftSize = captureMeta.FftSize;
        FrequencyHz = captureMeta.CentFreqHz;
        SamplingHz = captureMeta.SampFreqHz;
        Gain = captureMeta.GainDb;

        // Average the same way HiStreamingPipeline/Calibrator do: SKAO-normalized power
        // per fftSize frame, arithmetic mean via HiStreamingAccumulator.
        var acc = new HiStreamingAccumulator(ScanFftSize);
        int bytesPerChunk = ScanFftSize * 2; // adjust if your IQ format differs

        BeginProgress("Processing capture…");
        ForEachChunk(captureIq, bytesPerChunk, chunk =>
        {
            var spectrum = _fftEngine.ComputeSkAoPower(chunk, ScanFftSize);
            acc.AddCaptureFrame(spectrum);
        });

        // Same fix as ProcessBaseline: shift out of raw FFT-bin order before display.
        captureSpectrum = HiStreamingPipeline.FftShift(acc.GetCaptureAverage());
        rawCaptureSpectrum = captureSpectrum;
        if (DespikeEnabled)
            captureSpectrum = HiStreamingPipeline.Despike(captureSpectrum, SamplingHz, DespikeThresholdSigma);

        SpectrumVm.Mode = SpectrumMode.HiFrequency;

        SpectrumVm.UpdateParameters(ScanFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(captureSpectrum) : captureSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
    }

    /// <summary>
    /// Diagnostic dump of the raw-vs-despiked baseline/capture arrays cached by the last
    /// ProcessBaseline/ProcessCapture run (i.e. the standalone single-file views - run
    /// Generate Chart there first, with Baseline/Capture Only, not the combined
    /// HiFrequency/Ratio mode, since that goes through ProcessHiCore's pair-based despike
    /// instead and doesn't cache a "before" state). One row per FFT bin: bin index,
    /// frequency, and whichever of raw/despiked baseline/capture were generated. Written
    /// next to the source FITS file so it's easy to find, and readable directly off disk -
    /// no need to paste the contents anywhere. No CanExecute gating (unlike most commands
    /// here, which is deliberate - see e.g. ProcessBaseline/ProcessCapture's own early
    /// returns): this can run moments after a background Task.Run finishes populating the
    /// raw*Spectrum fields, and NotifyCanExecuteChanged from a background thread isn't
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

    private (double[] baselineSpectrum, double[] captureSpectrum, HiStreamingPipeline hi)  ProcessHiCore()
    {
        if (BaselineFile is null || CaptureFile is null)
            return (Array.Empty<double>(), Array.Empty<double>(), new HiStreamingPipeline());

        // --- 1. Load FITS IQ data ---
        var (baselineMeta, baselineIq) = _fitsFileIo.ReadRawIq(BaselineFile);
        var (captureMeta, captureIq) = ReadCombinedCaptureRawIq(CaptureFile);

        // --- 2. Validate metadata ---
        if (baselineMeta.SampFreqHz != captureMeta.SampFreqHz)
            throw new InvalidOperationException("Sample rates of baseline and capture files do not match.");

        if (baselineMeta.FftSize != captureMeta.FftSize)
            throw new InvalidOperationException("FFT sizes of baseline and capture files do not match.");

        ScanFftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;

        if (TargetFftSize == 0)
            TargetFftSize = ScanFftSize;   // default to no downscaling
        if (TargetFftSize > ScanFftSize)
            TargetFftSize = ScanFftSize;


        int bytesPerFrame = TargetFftSize * 2;

        if (TargetFftSize < ScanFftSize)
        {
            // Downscale the IQ data to the target FFT size
            baselineIq = IqDownscaler.Downscale(baselineIq, ScanFftSize, TargetFftSize);
            captureIq = IqDownscaler.Downscale(captureIq, ScanFftSize, TargetFftSize);
        }

        // --- 3. Create streaming accumulator ---
        var acc = new HiStreamingAccumulator(TargetFftSize);

        // --- 4. Accumulate baseline frames ---
        BeginProgress("Processing baseline…");
        ForEachChunk(baselineIq, bytesPerFrame, chunk =>
        {
            var spectrum = _fftEngine.ComputeSkAoPower(chunk, TargetFftSize);
            acc.AddBaselineFrame(spectrum);
        });

        // --- 5. Accumulate capture frames ---
        BeginProgress("Processing capture…");
        ForEachChunk(captureIq, bytesPerFrame, chunk =>
        {
            var spectrum = _fftEngine.ComputeSkAoPower(chunk, TargetFftSize);
            acc.AddCaptureFrame(spectrum);
        });

        FrameCount = $"{acc.BaselineFrames}/{acc.CaptureFrames}";
        // --- 6. Get averaged spectra ---
        var (baselineSpectrum, captureSpectrum) = acc.GetAveragedSpectra();

        // --- 7. LSR correction, from the capture's recorded pointing/time/site (the
        // baseline is just a terminator reading - its own pointing is meaningless here).
        double lsrCorrectionKmPerSec = captureMeta.ComputeLsrCorrectionKmPerSec();
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

        // --- Load FITS IQ ---
        var (baselineMeta, baselineIq) = _fitsFileIo.ReadRawIq(BaselineFile);
        var (captureMeta, captureIq) = ReadCombinedCaptureRawIq(CaptureFile);

        if (baselineMeta.SampFreqHz != captureMeta.SampFreqHz)
            throw new InvalidOperationException("Sample rates of baseline and capture files do not match.");

        if (baselineMeta.FftSize != captureMeta.FftSize)
            throw new InvalidOperationException("FFT sizes of baseline and capture files do not match.");

        ScanFftSize = baselineMeta.FftSize;
        TargetFftSize = 256;   // SKAO pipeline expects 256 complex samples
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;
        int targetFrames  = 16;

        int bytesPerFrame = ScanFftSize * 2;   // IQ: 2 bytes per complex sample
        int totalFrames = baselineIq.Length / bytesPerFrame;

        if (totalFrames < SkaoConstants.NumIntegrations)
            throw new InvalidOperationException(
                $"Baseline FITS does not contain enough frames. " +
                $"Need {SkaoConstants.NumIntegrations}, found {totalFrames}.");

        // No per-chunk loop is exposed here (SkaoHiObservation does its own internal
        // integration over a small, fixed-size slice), so just bracket the whole thing.
        BeginProgress("Processing SKAO TTRT…");

        baselineIq = IqDownscaler.Downscale(baselineIq, ScanFftSize, TargetFftSize);
        captureIq = IqDownscaler.Downscale(captureIq, ScanFftSize, TargetFftSize);

            FrameCount = $"{targetFrames}/{targetFrames}";

        // --- Run SKAO pipeline ---
        var skao = new SkaoHiObservation();
        skao.ProcessIq(
            baselineIq,
            captureIq,
            256,          // SKAO FFT size
            SamplingHz,
            FrequencyHz
        );

        ReportProgress(1.0);

        var hi = skao.Pipeline;

        // --- Update SpectrumViewModel ---
        SpectrumVm.Mode = SpectrumMode.HiFrequency;
        SpectrumVm.UpdateParameters(
            SkaoConstants.NumIntegrationBins,   // 256
            FrequencyHz,
            SamplingHz
        );

        SpectrumVm.UpdateSpectrum(
            hi.HiSpectrum,
            hi.FrequencyHz
        );
    }

}
