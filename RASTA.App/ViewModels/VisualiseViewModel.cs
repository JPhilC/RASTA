using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Astro;
using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.HiPipeline.RASTA.Processing.HiPipeline;
using RASTA.Processing.IfAverage;

namespace RASTA.App.ViewModels;


public partial class VisualiseViewModel : ObservableObject
{
    private readonly FitsFileIo _fitsFileIo;
    private readonly IFftEngine _fftEngine;
    private readonly StatusBarViewModel _statusBar;
    private IfAverageProcessor _baselineProcessor;
    private IfAverageProcessor _captureProcessor;

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
                    if (Mode == SpectrumMode.IF)
                        ProcessFilesIf();
                    else if (Mode == SpectrumMode.HiFrequency)
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
        var (baselineMeta, baselineIq) = _fitsFileIo.ReadRawIq(BaselineFile);
        ScanFftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;

        // Average the same way HiStreamingPipeline/Calibrator do: SKAO-normalized power
        // per fftSize frame, arithmetic mean via HiStreamingAccumulator - not IfAverageProcessor.
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

        SpectrumVm.Mode = SpectrumMode.IF;
        SpectrumVm.UpdateParameters(ScanFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(baselineSpectrum) : baselineSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
    }

    private void ProcessCapture()
    {
        if (CaptureFile is null)
            return;
        var (captureMeta, captureIq) = _fitsFileIo.ReadRawIq(CaptureFile);
        ScanFftSize = captureMeta.FftSize;
        FrequencyHz = captureMeta.CentFreqHz;
        SamplingHz = captureMeta.SampFreqHz;
        Gain = captureMeta.GainDb;

        // Average the same way HiStreamingPipeline/Calibrator do: SKAO-normalized power
        // per fftSize frame, arithmetic mean via HiStreamingAccumulator - not IfAverageProcessor.
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

        SpectrumVm.Mode = SpectrumMode.IF;

        SpectrumVm.UpdateParameters(ScanFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(UseDbScale ? ToDb(captureSpectrum) : captureSpectrum);
        SpectrumVm.YAxes[0].Name = UseDbScale ? "Power (dB)" : "Power";
    }

    private void ProcessFilesIf()
    {
        if (BaselineFile is null || CaptureFile is null)
            return;

        var (baselineMeta, baselineIq) = _fitsFileIo.ReadRawIq(BaselineFile);
        var (captureMeta, captureIq) = _fitsFileIo.ReadRawIq(CaptureFile);

        // Perform some checks to ensure that the baseline and capture files are compatible
        if (baselineMeta.SampFreqHz != captureMeta.SampFreqHz)
            throw new InvalidOperationException("Sample rates of baseline and capture files do not match.");

        //if (baselineMeta.CentFreqHz != captureMeta.CentFreqHz) 
        //    throw new InvalidOperationException("Center frequencies of baseline and capture files do not match.");

        if (baselineMeta.FftSize != captureMeta.FftSize)
            throw new InvalidOperationException("FFT sizes of baseline and capture files do not match.");

        ScanFftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;
        if (TargetFftSize == 0)
            TargetFftSize = ScanFftSize;   // default to no downscaling

        if (TargetFftSize< ScanFftSize)
        {
            // Downscale the IQ data to the target FFT size
            baselineIq = DownscaleIq(baselineIq, ScanFftSize, TargetFftSize);
            captureIq = DownscaleIq(captureIq, ScanFftSize, TargetFftSize);
        }

        // Generate the baseline spectrum from the baseline IQ data
        _baselineProcessor = new IfAverageProcessor(TargetFftSize);

        // For calibration, we want a very stable, flat baseline:
        _baselineProcessor.Median.Enabled = true;              // remove impulsive junk
        _baselineProcessor.Rfi.Enabled = true;                 // track bad frames
        _baselineProcessor.Intermediate.Window = 10;           // short-term average
        _baselineProcessor.LongTerm.Window = 50;               // long-term average
        _baselineProcessor.Background.SubractEnabled = false;         // no subtraction during calibration
        _baselineProcessor.Background.DivideEnabled = false;         // no division during calibration
        _baselineProcessor.LinearOutput = true;            // Output will be linear
        _baselineProcessor.SavitzkyGolay.Enabled = false;      // visually smooth baseline
        _baselineProcessor.Db.Offset = 0.0;

        baselineSpectrum = new double[TargetFftSize];

        // Break the long baseline capture into FFT-sized chunks
        int bytesPerChunk = TargetFftSize * 2; // adjust if your IQ format differs

        BeginProgress("Processing baseline…");
        ForEachChunk(baselineIq, bytesPerChunk, chunk =>
        {
            var spectrum = _fftEngine.ComputeSpectrum(chunk, TargetFftSize);

            // Feed each spectrum into IF_Average
            _baselineProcessor.Process(spectrum, baselineSpectrum);
        });


        // Initialise the IF_Average processor with the FFT size and calibration baseline spectrum
        _captureProcessor = new IfAverageProcessor(TargetFftSize);
        // Configure defaults
        _captureProcessor.Median.Enabled = true;
        _captureProcessor.Rfi.Enabled = true;
        _captureProcessor.Intermediate.Window = 10;
        _captureProcessor.LongTerm.Window = 20;
        _captureProcessor.Background.Load(baselineSpectrum);
        _captureProcessor.Background.SubractEnabled = true;
        _captureProcessor.Background.DivideEnabled = false;
        _captureProcessor.LinearOutput = false;
        _captureProcessor.SavitzkyGolay.Enabled = false;
        _captureProcessor.Db.Offset = 0.0;

        captureSpectrum = new double[TargetFftSize];

        BeginProgress("Processing capture…");
        ForEachChunk(captureIq, bytesPerChunk, chunk =>
        {
            var spectrum = _fftEngine.ComputeSpectrum(chunk, TargetFftSize);

            // Feed each spectrum into IF_Average
            _captureProcessor.Process(spectrum, captureSpectrum);
        });
        SpectrumVm.Mode = SpectrumMode.IF;

        SpectrumVm.UpdateParameters(TargetFftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(captureSpectrum); // Use actual center frequency and sample rate

    }

    private (double[] baselineSpectrum, double[] captureSpectrum, HiStreamingPipeline hi)  ProcessHiCore()
    {
        if (BaselineFile is null || CaptureFile is null)
            return (Array.Empty<double>(), Array.Empty<double>(), new HiStreamingPipeline());

        // --- 1. Load FITS IQ data ---
        var (baselineMeta, baselineIq) = _fitsFileIo.ReadRawIq(BaselineFile);
        var (captureMeta, captureIq) = _fitsFileIo.ReadRawIq(CaptureFile);

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
        var (captureMeta, captureIq) = _fitsFileIo.ReadRawIq(CaptureFile);

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
