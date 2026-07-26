namespace RASTA.Core.Sdr;

public interface ISdrDevice : IAsyncDisposable
{
    // -----------------------------
    // RAW IQ capture (true RAW)
    // -----------------------------
    Task<byte[]> CaptureRawIqAsync(double frequencyHz, double sampleRateHz, double gainDb, uint sampleCount, CancellationToken ct);

    // -----------------------------
    // Spectrum capture (FFT)
    // -----------------------------
    Task<double[]> CaptureSpectrumAsync(double frequencyHz, double sampleRateHz, double gainDb,
        TimeSpan dwellTime,
        int fftSize,
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
    void SetPpmCorrection(int ppm);
    void SetDirectSampling(bool enabled);
}
