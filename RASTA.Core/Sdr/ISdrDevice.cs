namespace RASTA.Core.Sdr;

public interface ISdrDevice
{
    void Configure(double centerFreqHz, double sampleRateHz, int gain);
    IAsyncEnumerable<System.Numerics.Complex[]> CaptureBlocksAsync(
        int blockSize,
        CancellationToken ct);
}
