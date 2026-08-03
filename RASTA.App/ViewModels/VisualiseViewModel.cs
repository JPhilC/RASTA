using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private int fftSize;

    [ObservableProperty]
    private double frequencyHz;

    [ObservableProperty]
    private double samplingHz;

    [ObservableProperty]
    private double gain;

    public SpectrumViewModel SpectrumVm { get; private set; }


    public VisualiseViewModel(FitsFileIo fits, IFftEngine fftEngine)
    {
        _fitsFileIo = fits;
        _fftEngine = fftEngine;
        SpectrumVm = new SpectrumViewModel(4096, 1420_405_800, 2.4e6); // default values; will be updated when calibration is loaded
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
    private void GenerateChart()
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
        FftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;
        // Generate the baseline spectrum from the baseline IQ data
        _baselineProcessor = new IfAverageProcessor(FftSize);
        // For calibration, we want a very stable, flat baseline:
        _baselineProcessor.Median.Enabled = true;              // remove impulsive junk
        _baselineProcessor.Rfi.Enabled = true;                 // track bad frames
        _baselineProcessor.Intermediate.Window = 10;           // short-term average
        _baselineProcessor.LongTerm.Window = 50;               // long-term average
        _baselineProcessor.Background.SubractEnabled = false;         // no subtraction during calibration
        _baselineProcessor.Background.DivideEnabled = false;         // no division during calibration
        _baselineProcessor.LinearOutput = true;
        _baselineProcessor.SavitzkyGolay.Enabled = false;      // visually smooth baseline
        _baselineProcessor.Db.Offset = 0.0;
        baselineSpectrum = new double[FftSize];
        // Break the long baseline capture into FFT-sized chunks
        int bytesPerChunk = FftSize * 2; // adjust if your IQ format differs
        for (int offset = 0; offset + bytesPerChunk <= baselineIq.Length; offset += bytesPerChunk)
        {
            var chunk = new byte[bytesPerChunk];
            Buffer.BlockCopy(baselineIq, offset, chunk, 0, bytesPerChunk);
            var spectrum = _fftEngine.ComputeSpectrum(chunk, FftSize);
            // Feed each spectrum into IF_Average
            _baselineProcessor.Process(spectrum, baselineSpectrum);
        }

        SpectrumVm.Mode = SpectrumMode.IF;
        SpectrumVm.UpdateParameters(FftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(baselineSpectrum); // Use actual center frequency and sample rate
    }

    private void ProcessCapture()
    {
        if (CaptureFile is null)
            return;
        var (captureMeta, captureIq) = _fitsFileIo.ReadRawIq(CaptureFile);
        FftSize = captureMeta.FftSize;
        FrequencyHz = captureMeta.CentFreqHz;
        SamplingHz = captureMeta.SampFreqHz;
        Gain = captureMeta.GainDb;
        // Initialise the IF_Average processor with the FFT size and calibration baseline spectrum
        _captureProcessor = new IfAverageProcessor(FftSize);
        // Configure defaults
        _captureProcessor.Median.Enabled = true;
        _captureProcessor.Rfi.Enabled = true;
        _captureProcessor.Intermediate.Window = 10;
        _captureProcessor.LongTerm.Window = 20;
        _captureProcessor.Background.SubractEnabled = false;
        _captureProcessor.Background.DivideEnabled = false;
        _captureProcessor.LinearOutput = true;
        _captureProcessor.SavitzkyGolay.Enabled = false;
        _captureProcessor.Db.Offset = 0.0;

        captureSpectrum = new double[FftSize];

        int bytesPerChunk = FftSize * 2; // adjust if your IQ format differs
        for (int offset = 0; offset + bytesPerChunk <= captureIq.Length; offset += bytesPerChunk)
        {
            var chunk = new byte[bytesPerChunk];
            Buffer.BlockCopy(captureIq, offset, chunk, 0, bytesPerChunk);

            var spectrum = _fftEngine.ComputeSpectrum(chunk, FftSize);

            // Feed each spectrum into IF_Average
            _captureProcessor.Process(spectrum, captureSpectrum);
        }

        SpectrumVm.Mode = SpectrumMode.IF;

        SpectrumVm.UpdateParameters(FftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(captureSpectrum); // Use actual center frequency and sample rate
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

        FftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;

        // Generate the baseline spectrum from the baseline IQ data
        _baselineProcessor = new IfAverageProcessor(FftSize);

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

        baselineSpectrum = new double[FftSize];

        // Break the long baseline capture into FFT-sized chunks
        int bytesPerChunk = FftSize * 2; // adjust if your IQ format differs
        for (int offset = 0; offset + bytesPerChunk <= baselineIq.Length; offset += bytesPerChunk)
        {
            var chunk = new byte[bytesPerChunk];
            Buffer.BlockCopy(baselineIq, offset, chunk, 0, bytesPerChunk);

            var spectrum = _fftEngine.ComputeSpectrum(chunk, FftSize);

            // Feed each spectrum into IF_Average
            _baselineProcessor.Process(spectrum, baselineSpectrum);
        }


        // Initialise the IF_Average processor with the FFT size and calibration baseline spectrum
        _captureProcessor = new IfAverageProcessor(FftSize);
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

        captureSpectrum = new double[FftSize];

        for (int offset = 0; offset + bytesPerChunk <= captureIq.Length; offset += bytesPerChunk)
        {
            var chunk = new byte[bytesPerChunk];
            Buffer.BlockCopy(captureIq, offset, chunk, 0, bytesPerChunk);

            var spectrum = _fftEngine.ComputeSpectrum(chunk, FftSize);

            // Feed each spectrum into IF_Average
            _captureProcessor.Process(spectrum, captureSpectrum);
        }
        SpectrumVm.Mode = SpectrumMode.IF;

        SpectrumVm.UpdateParameters(FftSize, FrequencyHz, SamplingHz);
        // Update the SpectrumViewModel with the new data
        SpectrumVm.UpdateSpectrum(captureSpectrum); // Use actual center frequency and sample rate

    }

    private (double[] baselineSpectrum, double[] captureSpectrum, HiStreamingPipeline hi)
    ProcessHiCore()
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

        FftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;

        int bytesPerFrame = FftSize * 2;

        // --- 3. Create streaming accumulator ---
        var acc = new HiStreamingAccumulator(FftSize);

        // --- 4. Accumulate baseline frames ---
        for (int offset = 0; offset + bytesPerFrame <= baselineIq.Length; offset += bytesPerFrame)
        {
            var chunk = new byte[bytesPerFrame];
            Buffer.BlockCopy(baselineIq, offset, chunk, 0, bytesPerFrame);

            var spectrum = _fftEngine.ComputeSkAoPower(chunk, FftSize);
            acc.AddBaselineFrame(spectrum);
        }

        // --- 5. Accumulate capture frames ---
        for (int offset = 0; offset + bytesPerFrame <= captureIq.Length; offset += bytesPerFrame)
        {
            var chunk = new byte[bytesPerFrame];
            Buffer.BlockCopy(captureIq, offset, chunk, 0, bytesPerFrame);

            var spectrum = _fftEngine.ComputeSkAoPower(chunk, FftSize);
            acc.AddCaptureFrame(spectrum);
        }

        // --- 6. Get averaged spectra ---
        var (baselineSpectrum, captureSpectrum) = acc.GetAveragedSpectra();

        // --- 7. Run streaming HI pipeline ---
        var hi = new HiStreamingPipeline();
        hi.Process(
            baselineSpectrum,
            captureSpectrum,
            sampleRateHz: SamplingHz,
            centerFreqHz: FrequencyHz
        );

        return (baselineSpectrum, captureSpectrum, hi);
    }

    private void ProcessFilesHiVelocity()
    {
        var (_, _, hi) = ProcessHiCore();

        SpectrumVm.Mode = SpectrumMode.HiVelocity;
        SpectrumVm.UpdateParameters(FftSize, FrequencyHz, SamplingHz);

        SpectrumVm.UpdateSpectrum(hi.HiSpectrum, hi.VelocityKmPerSec);
    }

    private void ProcessFilesHiFrequency()
    {
        var (_, _, hi) = ProcessHiCore();

        SpectrumVm.Mode = SpectrumMode.HiFrequency;
        SpectrumVm.UpdateParameters(FftSize, FrequencyHz, SamplingHz);

        SpectrumVm.UpdateSpectrum(hi.HiSpectrum, hi.FrequencyHz);
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

        FftSize = baselineMeta.FftSize;
        FrequencyHz = baselineMeta.CentFreqHz;
        SamplingHz = baselineMeta.SampFreqHz;
        Gain = baselineMeta.GainDb;

        int bytesPerFrame = FftSize * 2;   // IQ: 2 bytes per complex sample
        int totalFrames = baselineIq.Length / bytesPerFrame;

        if (totalFrames < SkaoConstants.NumIntegrations)
            throw new InvalidOperationException(
                $"Baseline FITS does not contain enough frames. " +
                $"Need {SkaoConstants.NumIntegrations}, found {totalFrames}.");

        // --- Downsample IQ from N → 256 complex samples ---
        byte[] DownsampleIq(byte[] iqFrame, int originalFftSize)
        {
            const int targetFft = 256;
            int factor = originalFftSize / targetFft;   // e.g. 4096/256 = 16

            var result = new byte[targetFft * 2];       // 256 complex samples → 512 bytes

            for (int i = 0; i < targetFft; i++)
            {
                int start = i * factor;

                double sumI = 0;
                double sumQ = 0;

                for (int j = 0; j < factor; j++)
                {
                    int idx = (start + j) * 2;
                    sumI += iqFrame[idx];
                    sumQ += iqFrame[idx + 1];
                }

                result[i * 2] = (byte)(sumI / factor);
                result[i * 2 + 1] = (byte)(sumQ / factor);
            }

            return result;
        }

        // --- Build 16 SKAO frames ---
        var baselineFrames256 = new List<byte[]>();
        var captureFrames256 = new List<byte[]>();

        for (int i = 0; i < SkaoConstants.NumIntegrations; i++)
        {
            var chunkBase = new byte[bytesPerFrame];
            var chunkCapt = new byte[bytesPerFrame];

            Buffer.BlockCopy(baselineIq, i * bytesPerFrame, chunkBase, 0, bytesPerFrame);
            Buffer.BlockCopy(captureIq, i * bytesPerFrame, chunkCapt, 0, bytesPerFrame);

            baselineFrames256.Add(DownsampleIq(chunkBase, FftSize));
            captureFrames256.Add(DownsampleIq(chunkCapt, FftSize));
        }

        // --- Flatten into SKAO byte[] slices ---
        var baselineSlice = baselineFrames256.SelectMany(x => x).ToArray();
        var captureSlice = captureFrames256.SelectMany(x => x).ToArray();

        // --- Run SKAO pipeline ---
        var skao = new SkaoHiObservation();
        skao.ProcessIq(
            baselineSlice,
            captureSlice,
            256,          // SKAO FFT size
            SamplingHz,
            FrequencyHz
        );

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
