using RASTA.Core.Calibration;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;
namespace RASTA.Processing.Calibration;

public class Calibrator
{
    private readonly ISdrDevice _sdr;
    private readonly IFftEngine _fft;

    public async Task<CalibrationProfile> RunAsync(
        double centerFreqHz,
        double sampleRateHz,
        int gain,
        TimeSpan duration,
        int fftSize,
        CancellationToken ct)
    {
        _sdr.Configure(centerFreqHz, sampleRateHz, gain);

        int blocksNeeded = (int)(duration.TotalSeconds * sampleRateHz / fftSize);
        var accumulator = new double[fftSize];
        int count = 0;

        await foreach (var block in _sdr.CaptureBlocksAsync(fftSize, ct))
        {
            var spectrum = _fft.PowerSpectrum(block);
            for (int i = 0; i < fftSize; i++)
                accumulator[i] += spectrum[i];

            if (++count >= blocksNeeded)
                break;
        }

        for (int i = 0; i < fftSize; i++)
            accumulator[i] /= count;

        double avgNoise = accumulator.Average();
        double gainFactor = 1.0 / avgNoise;

        return new CalibrationProfile
        {
            TimestampUtc = DateTime.UtcNow,
            CenterFrequencyHz = centerFreqHz,
            SampleRateHz = sampleRateHz,
            FftSize = fftSize,
            NoiseSpectrum = accumulator,
            GainFactor = gainFactor
        };
    }
}
