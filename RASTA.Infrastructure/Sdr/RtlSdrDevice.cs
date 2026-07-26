using RASTA.Core.Sdr;
using RtlSdrManager;
using RtlSdrManager.Exceptions;
using RtlSdrManager.Modes;

namespace RASTA.Infrastructure.Sdr;


public sealed class RtlSdrDevice : ISdrDevice
{
    private RtlSdrManagedDevice? _device;

    private string _tunerType = "Unknown";
    private double _actualFrequencyHz;
    private double _actualSampleRateHz;
    private double _currentGainDb;
    private readonly List<double> _supportedGains = new();

    public IReadOnlyList<double> SupportedGainsDb => _supportedGains;

    public double ActualFrequencyHz => _actualFrequencyHz;
    public double ActualSampleRateHz => _actualSampleRateHz;
    public string TunerType => _tunerType;

    public string DeviceId => _tunerType;

    public RtlSdrDevice()
    {
        // Probe device once to populate supported gains
        try
        {
            // RtlSdrDeviceManager.SuppressLibraryConsoleOutput = true;
            var manager = RtlSdrDeviceManager.Instance;
            manager.OpenManagedDevice(0, "probe");
            var dev = manager["probe"];

            _supportedGains.Clear();
            _supportedGains.AddRange(dev.SupportedTunerGains);

            _tunerType = dev.TunerType.ToString();

            manager.CloseManagedDevice("probe");
        }
        catch
        {
            // Device not connected or driver missing
            // Leave SupportedGainsDb empty
        }
    }



    public async Task<byte[]> CaptureRawIqAsync(
    double frequencyHz,
    double sampleRateHz,
    double gainDb,
    uint sampleCount,
    CancellationToken ct)
    {
        var manager = RtlSdrDeviceManager.Instance;
        manager.OpenManagedDevice(0, "rasta-rtl");
        _device = manager["rasta-rtl"];

        try
        {
            // Configure device
            _device.CenterFrequency = Frequency.FromHz(frequencyHz);
            _device.SampleRate = Frequency.FromHz(sampleRateHz);

            _device.TunerGainMode = TunerGainModes.Manual;
            _device.TunerGain = gainDb;   // gain stays double, no clamping

            _actualFrequencyHz = _device.CenterFrequency.Hz;
            _actualSampleRateHz = _device.SampleRate.Hz;

            // Async buffer mode
            _device.UseRawBufferMode = true;
            _device.MaxAsyncBufferSize = 512 * 1024;
            _device.DropSamplesOnFullBuffer = true;

            _device.ResetDeviceBuffer();

            ulong bytesNeeded = (ulong)sampleCount * 2UL;
            var output = new byte[bytesNeeded];
            ulong writePos = 0;

            // Start async reader
            _device.StartReadSamplesAsync();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _device.SamplesAvailable += (sender, args) =>
            {
                var buf = _device.GetRawSamplesFromAsyncBuffer();
                if (buf == null) return;

                try
                {
                    ReadOnlySpan<byte> raw = buf.Data.AsSpan(0, buf.ByteLength);

                    ulong toCopy = Math.Min((ulong)raw.Length, bytesNeeded - writePos);
                    raw.Slice(0, (int)toCopy).CopyTo(output.AsSpan((int)writePos));
                    writePos += toCopy;

                    if (writePos >= bytesNeeded)
                        tcs.TrySetResult(true);
                }
                finally
                {
                    buf.Return();
                }
            };

            // Wait until buffer is full, with a timeout of dwell + 5s safety margin
            var dwellTimeout = TimeSpan.FromSeconds((double)sampleCount / sampleRateHz + 5.0);
            await tcs.Task.WaitAsync(dwellTimeout, ct);

            return output;
        }
        finally
        {
            try { _device?.StopReadSamplesAsync(); } catch { }
            try { manager.CloseManagedDevice("rasta-rtl"); } catch { }
            _device = null;
        }
    }

    public Task<double[]> CaptureSpectrumAsync(double frequencyHz, double sampleRateHz, double gainDb,
        TimeSpan dwellTime,
        int fftSize,
        CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            uint sampleCount = (uint)(_actualSampleRateHz * dwellTime.TotalSeconds);

            var rawIq = await CaptureRawIqAsync(frequencyHz, sampleRateHz, gainDb, sampleCount, ct).ConfigureAwait(false);

            // Here you call into your Processing/Core FFT pipeline.
            // For now, this is a placeholder you’ll replace with your real implementation.
            //
            // Example:
            // return _spectrumTransformer.ComputeSpectrumFromIq(rawIq, _actualSampleRateHz, fftSize);

            var spectrum = new double[fftSize];
            return spectrum;
        }, ct);
    }

    public void SetBiasTee(bool enabled)
    {
        if (_device is null)
            throw new InvalidOperationException("SDR not initialized.");

        _device.SetBiasTee(enabled ? BiasTeeModes.Enabled : BiasTeeModes.Disabled);
    }

    public void SetPpmCorrection(int ppm)
    {
        // Not supported by this RtlSdrManager version; no-op for now.
    }

    public void SetDirectSampling(bool enabled)
    {
        // Direct sampling mode not exposed in this RtlSdrManager version; no-op for now.
    }

    public ValueTask DisposeAsync()
    {
        if (_device is not null)
        {
            var manager = RtlSdrDeviceManager.Instance;
            manager.CloseManagedDevice("rasta-rtl");
            _device = null;
        }

        return ValueTask.CompletedTask;
    }
}

