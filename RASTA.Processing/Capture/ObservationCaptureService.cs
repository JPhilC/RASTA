using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RASTA.Core.Calibration;
using RASTA.Core.Capture;
using RASTA.Core.Sdr;
using RASTA.Core.Processing;

namespace RASTA.Processing.Capture
{
    public sealed class ObservationCaptureService
    {
        private readonly ISdrDevice _sdr;
        private readonly IFftEngine _fft;

        public ObservationCaptureService(ISdrDevice sdr, IFftEngine fft)
        {
            _sdr = sdr;
            _fft = fft;
        }

        public async Task<ObservationRecord> CaptureAsync(
            TargetPoint pointing,
            CalibrationProfile calibration,
            TimeSpan integrationTime,
            CancellationToken ct)
        {

            uint fftSize = calibration.FftSize;

            uint totalSamples = (uint)(calibration.SampleRateHz * integrationTime.TotalSeconds);
            uint blocksNeeded = totalSamples / fftSize;

            var accumulator = new double[fftSize];
            int count = 0;

            while (count < blocksNeeded)
            {
                ct.ThrowIfCancellationRequested();

                var rawIq = await _sdr.CaptureRawIqAsync(calibration.CenterFrequencyHz, calibration.SampleRateHz, calibration.GainDb, fftSize, ct).ConfigureAwait(false);

                var block = new Complex[fftSize];
                for (int i = 0; i < fftSize; i++)
                {
                    double iSample = rawIq[2 * i] - 128;
                    double qSample = rawIq[2 * i + 1] - 128;
                    block[i] = new Complex(iSample, qSample);
                }

                var spectrum = _fft.PowerSpectrum(block);

                for (int i = 0; i < fftSize; i++)
                    accumulator[i] += spectrum[i];

                count++;
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

}
