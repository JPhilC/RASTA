using RASTA.Core.Processing;

namespace RASTA.Core.Sdr;

public interface ISdrDevice : IDisposable
{
    event Action<byte[]>? RawIqChunkAvailable;

    Task StartStreamingAsync(double frequencyHz, double sampleRateHz, double gainDb, CancellationToken ct);
   
    Task StopStreamingAsync();


    // -----------------------------
    // RAW IQ capture (true RAW)
    // -----------------------------
    Task<byte[]> CaptureRawIqAsync(double frequencyHz, double sampleRateHz, double gainDb, uint sampleCount, CancellationToken ct, Action<double>? onProgress = null);

    // -----------------------------
    // Spectrum capture (FFT)
    // -----------------------------
    Task<double[]> CaptureSpectrumAsync(double frequencyHz, double sampleRateHz, double gainDb,
        TimeSpan dwellTime,
        int fftSize,
        IFftEngine fftEngine,
        CancellationToken ct);

    // -----------------------------
    // Device capabilities & metadata
    // -----------------------------
    IReadOnlyList<double> SupportedGainsDb { get; }

    double ActualFrequencyHz { get; }
    double ActualSampleRateHz { get; }  
    string TunerType { get; }
    string DeviceId { get; }

    // -----------------------------
    // Optional advanced features
    // -----------------------------
    void SetBiasTee(bool enabled);
    /// <summary>
    /// Sets the whole-ppm crystal correction the tuner applies (see FrequencyCorrection).
    /// Stored, and applied the next time the device is tuned (StartStreamingAsync /
    /// CaptureRawIqAsync), so call it before each capture - including on a device instance
    /// that has just been reopened, which starts at 0.
    /// </summary>
    void SetPpmCorrection(int ppm);
    void SetDirectSampling(bool enabled);
}
