using RASTA.Core.Calibration;
using RASTA.Core.Capture;
using RASTA.Core.Sdr;
using RASTA.Core.Processing;

namespace RASTA.Processing.Capture;

public class ObservationCaptureService
{
    private readonly ISdrDevice _sdr;
    private readonly IFftEngine _fft;

    public async Task<ObservationRecord> CaptureAsync(
        TargetPoint pointing,
        CalibrationProfile calibration,
        TimeSpan integrationTime,
        CancellationToken ct)
    {
        _sdr.Configure(calibration.CenterFrequencyHz,
                       calibration.SampleRateHz,
                       gain: 0);

        int fftSize = calibration.FftSize;
        int blocksNeeded = (int)(integrationTime.TotalSeconds *
                                 calibration.SampleRateHz / fftSize);

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
            accumulator[i] = calibration.GainFactor * (accumulator[i] / count);

        var metadata = new ObservationMetadata
        {
            TimestampUtc = DateTime.UtcNow,
            Pointing = pointing,
            IntegrationTime = integrationTime,
            CenterFrequencyHz = calibration.CenterFrequencyHz,
            SampleRateHz = calibration.SampleRateHz
        };

        return new ObservationRecord
        {
            Metadata = metadata,
            AveragedSpectrum = accumulator
        };
    }
}
