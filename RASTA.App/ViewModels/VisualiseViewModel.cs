using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Astro;
using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.HiPipeline.RASTA.Processing.HiPipeline;
using System.IO;
using System.Text.RegularExpressions;

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

    public SpectrumViewModel SpectrumVm { get; private set; }


    public VisualiseViewModel(FitsFileIo fits, IFftEngine fftEngine, StatusBarViewModel statusBar)
    {
        _fitsFileIo = fits;
        _fftEngine = fftEngine;
        _statusBar = statusBar;
        SpectrumVm = new SpectrumViewModel(4096, 1420_405_800, 2.4e6); // default values; will be updated when calibration is loaded
    }

    // ---------------------------------------------------------
    // Progress reporting - real, measured progress (chunks
    // processed / total chunks), same pattern as Calibrator and
    // ObserveViewModel, not a time-based guess.
    // ---------------------------------------------------------

    private void BeginProgress(string status)
    {
        _statusBar.CaptureStatus = status;
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

    /// <summary>
    /// Iterates fixed-size chunks of raw IQ, invoking processChunk on each and reporting
    /// real, measured progress (chunks processed / total chunks) as it goes.
    /// </summary>
    private void ForEachChunk(byte[] iq, int bytesPerChunk, Action<byte[]> processChunk)
    {
        int totalChunks = bytesPerChunk > 0 ? iq.Length / bytesPerChunk : 0;
        int processedChunks = 0;

        for (int offset = 0; offset + bytesPerChunk <= iq.Length; offset += bytesPerChunk)
        {
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

    private static readonly Regex MultiFileCapturePattern =
        new(@"^(?<base>.+)_(?<index>\d+)of(?<total>\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// If the given capture file's name matches the ObserveViewModel-generated
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

        var match = MultiFileCapturePattern.Match(fileNameNoExt);
        if (!match.Success)
            return new List<string> { captureFilePath };

        string basePart = match.Groups["base"].Value;
        int total = int.Parse(match.Groups["total"].Value);

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
    /// CombinedFileCount so the UI shows how many files went into the result.
    ///
    /// Each file's IQ is trimmed to a whole number of its own native FFT frames
    /// before being appended, so a frame extracted later by the caller's chunking
    /// loop can never straddle the boundary between two separate (and physically
    /// discontinuous) captures.
    /// </summary>
    private (FitsFileMetaData meta, byte[] iq) ReadCombinedCaptureRawIq(string captureFilePath)
    {
        var files = ResolveRelatedCaptureFiles(captureFilePath);
        CombinedFileCount = files.Count;

        FitsFileMetaData? combinedMeta = null;
        var buffers = new List<byte[]>(files.Count);

        for (int f = 0; f < files.Count; f++)
        {
            BeginProgress(files.Count > 1
                ? $"Reading capture file {f + 1} of {files.Count}…"
                : "Reading capture file…");

            var (meta, iq) = _fitsFileIo.ReadRawIq(files[f]);

            if (combinedMeta == null)
            {
                combinedMeta = meta;
            }
            else
            {
                if (meta.FftSize != combinedMeta.FftSize ||
                    meta.SampFreqHz != combinedMeta.SampFreqHz ||
                    meta.CentFreqHz != combinedMeta.CentFreqHz)
                {
                    throw new InvalidOperationException(
                        $"Related capture file '{Path.GetFileName(files[f])}' has a different FFT size, " +
                        "sample rate, or center frequency than the other files being combined.");
                }

                // Total integration time across all combined files, not just the first.
                combinedMeta.DwellTimeSec += meta.DwellTimeSec;
            }

            int bytesPerNativeFrame = meta.FftSize * 2;
            int usableLength = (iq.Length / bytesPerNativeFrame) * bytesPerNativeFrame;
            if (usableLength != iq.Length)
            {
                var trimmed = new byte[usableLength];
                Buffer.BlockCopy(iq, 0, trimmed, 0, usableLength);
                iq = trimmed;
            }

            buffers.Add(iq);

            ReportProgress((double)(f + 1) / files.Count);
        }

        int totalLength = 0;
        foreach (var buf in buffers) totalLength += buf.Length;

        var combined = new byte[totalLength];
        int offset = 0;
        foreach (var buf in buffers)
        {
            Buffer.BlockCopy(buf, 0, combined, offset, buf.Length);
            offset += buf.Length;
        }

        return (combinedMeta!, combined);
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


    [RelayCommand]
    private async Task GenerateChartAsync()
    {
        if (BaselineFile is null && CaptureFile is null)
            return;

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
            });

            _statusBar.CaptureStatus = "Completed";
        }
        finally
        {
            EndProgress();
        }
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

        SpectrumVm.Mode = SpectrumMode.HiFrequency;

        SpectrumVm.UpdateParameters(ScanFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(captureSpectrum) : captureSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
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
            baselineIq = DownscaleIq(baselineIq, ScanFftSize, TargetFftSize);
            captureIq = DownscaleIq(captureIq, ScanFftSize, TargetFftSize);
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
        double lsrCorrectionKmPerSec = TryComputeLsrCorrectionKmPerSec(captureMeta);
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
            lsrCorrectionKmPerSec: lsrCorrectionKmPerSec
        );

        return (baselineSpectrum, captureSpectrum, hi);
    }

    /// <summary>
    /// Computes the LSR correction (km/s) for a captured file's pointing/time/site, or
    /// 0 if the FITS metadata doesn't have enough recorded to compute it (e.g. older
    /// files, or a capture with no site configured). Reconstructs RA/Dec from Az/Alt if
    /// the file was captured in AltAz mode rather than Equatorial.
    /// </summary>
    private static double TryComputeLsrCorrectionKmPerSec(FitsFileMetaData meta)
    {
        if (meta.SiteLatitudeDeg is not double lat || meta.SiteLongitudeDeg is not double lon)
            return 0.0;
        if (meta.ObservationDate == DateTime.MinValue)
            return 0.0;

        double raHours, decDeg;

        if (meta.RaDeg is double raDeg && meta.DecDeg is double dec)
        {
            raHours = raDeg / 15.0;
            decDeg = dec;
        }
        else if (meta.AzDeg is double az && meta.AltDeg is double alt)
        {
            (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(az, alt, meta.ObservationDate, lat, lon);
        }
        else
        {
            return 0.0; // no pointing recorded at all
        }

        return AstronomyUtils.ComputeLsrCorrectionKmPerSec(raHours, decDeg, meta.ObservationDate, lat, lon);
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

        baselineIq = DownscaleIq(baselineIq, ScanFftSize, TargetFftSize);
        captureIq = DownscaleIq(captureIq, ScanFftSize, TargetFftSize);

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

    /// <summary>
    /// Downscale raw IQ frames from originalFftSize → targetFftSize,
    /// automatically determining the number of frames from the input length.
    /// </summary>
    public static byte[] DownscaleIq(byte[] iq, int originalFftSize, int targetFftSize)
    {
        int bytesPerFrameIn = originalFftSize * 2;      // IQ: 2 bytes per complex sample
        int bytesPerFrameOut = targetFftSize * 2;

        // Floor the number of frames, ignore any trailing partial frame
        int numFrames = iq.Length / bytesPerFrameIn;
        if (numFrames == 0)
            throw new InvalidOperationException(
                $"IQ buffer length {iq.Length} is too small for even one frame ({bytesPerFrameIn} bytes each).");

        int factor = originalFftSize / targetFftSize;
        if (originalFftSize % targetFftSize != 0)
            throw new InvalidOperationException(
                $"FFT downscale must be integer ratio: {originalFftSize} → {targetFftSize}");

        var output = new byte[numFrames * bytesPerFrameOut];

        byte[] DownsampleFrame(byte[] frame)
        {
            var result = new byte[bytesPerFrameOut];

            for (int i = 0; i < targetFftSize; i++)
            {
                int start = i * factor;

                double sumI = 0;
                double sumQ = 0;

                for (int j = 0; j < factor; j++)
                {
                    int idx = (start + j) * 2;
                    sumI += frame[idx];
                    sumQ += frame[idx + 1];
                }

                result[i * 2] = (byte)(sumI / factor);
                result[i * 2 + 1] = (byte)(sumQ / factor);
            }

            return result;
        }

        for (int f = 0; f < numFrames; f++)
        {
            var frameIn = new byte[bytesPerFrameIn];
            Buffer.BlockCopy(iq, f * bytesPerFrameIn, frameIn, 0, bytesPerFrameIn);

            var frameOut = DownsampleFrame(frameIn);

            Buffer.BlockCopy(frameOut, 0, output, f * bytesPerFrameOut, bytesPerFrameOut);
        }

        return output;
    }


    
}
