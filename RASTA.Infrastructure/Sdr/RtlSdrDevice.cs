using RASTA.Core.Processing;
using RASTA.Core.Sdr;
using RtlSdrManager;
using RtlSdrManager.Modes;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RASTA.Infrastructure.Sdr;

public sealed class RtlSdrDevice : ISdrDevice, IDisposable
{
    public event Action<byte[]>? RawIqChunkAvailable;

    private EventHandler<SamplesAvailableEventArgs>? _streamingHandler;
    private bool _isStreaming;

    private readonly uint _deviceIndex;
    private RtlSdrManagedDevice? _device;

    private string _tunerType = "Unknown";
    private double _actualFrequencyHz;
    private double _actualSampleRateHz;
    private double _currentGainDb;

    private readonly List<double> _supportedGains = new();

    public uint DeviceIndex => _deviceIndex;
    public IReadOnlyList<double> SupportedGainsDb => _supportedGains;
    public string TunerType => _tunerType;

    public double ActualFrequencyHz => _actualFrequencyHz;
    public double ActualSampleRateHz => _actualSampleRateHz;

    public string DeviceId => $"{_tunerType} (Index {_deviceIndex})";

    // ---------------------------------------------------------
    // Constructor: does NOT open the device
    // ---------------------------------------------------------
    public RtlSdrDevice(uint deviceIndex)
    {
        _deviceIndex = deviceIndex;
    }

    // ---------------------------------------------------------
    // Attach the already-opened managed device
    // ---------------------------------------------------------
    public void AttachManagedDevice(RtlSdrManagedDevice dev)
    {
        _device = dev;

        _supportedGains.Clear();
        _supportedGains.AddRange(dev.SupportedTunerGains);

        _tunerType = dev.TunerType.ToString();
    }

    // ---------------------------------------------------------
    // Start Streaming: sets up the device and starts async streaming, raising RawIqChunkAvailable events
    // ---------------------------------------------------------

    public async Task StartStreamingAsync(double frequencyHz, double sampleRateHz, double gainDb, CancellationToken ct)
    {
        if (_device == null)
            throw new InvalidOperationException("SDR device not initialized.");
        if (_isStreaming)
            return;

        // Configure device for streaming
        _device.CenterFrequency = Frequency.FromHz(frequencyHz);
        _device.SampleRate = Frequency.FromHz(sampleRateHz);
        _device.TunerGainMode = TunerGainModes.Manual;
        _device.TunerGain = gainDb;

        _actualFrequencyHz = _device.CenterFrequency.Hz;
        _actualSampleRateHz = _device.SampleRate.Hz;
        _currentGainDb = gainDb;

        _device.ResetDeviceBuffer();
        _device.UseRawBufferMode = true;
        _device.MaxAsyncBufferSize = 512 * 1024;
        _device.DropSamplesOnFullBuffer = true;

        _streamingHandler = (sender, args) =>
        {
            var buf = _device.GetRawSamplesFromAsyncBuffer();
            if (buf == null) return;

            try
            {
                ReadOnlySpan<byte> raw = buf.Data.AsSpan(0, buf.ByteLength);
                var chunkCopy = new byte[raw.Length];
                raw.CopyTo(chunkCopy);

                RawIqChunkAvailable?.Invoke(chunkCopy);
            }
            finally
            {
                buf.Return();
            }
        };

        _device.SamplesAvailable += _streamingHandler;

        _device.StartReadSamplesAsync();
        _isStreaming = true;

        await Task.CompletedTask;
    }


    // ---------------------------------------------------------
    // Start Streaming: sets up the device and starts async streaming, raising RawIqChunkAvailable events
    // ---------------------------------------------------------

    public async Task StopStreamingAsync()
    {
        if (_device == null || !_isStreaming)
            return;

        try
        {
            _device.StopReadSamplesAsync();
            _device.ResetDeviceBuffer();
        }
        catch { }

        if (_streamingHandler != null)
            _device.SamplesAvailable -= _streamingHandler;

        _streamingHandler = null;
        _isStreaming = false;

        await Task.CompletedTask;
    }


    // ---------------------------------------------------------
    // Raw IQ capture
    // ---------------------------------------------------------
    public async Task<byte[]> CaptureRawIqAsync(
    double frequencyHz,
    double sampleRateHz,
    double gainDb,
    uint sampleCount,
    CancellationToken ct)
    {
        if (_device == null)
            throw new InvalidOperationException("SDR device not initialized.");

        // Configure device
        _device.CenterFrequency = Frequency.FromHz(frequencyHz);
        _device.SampleRate = Frequency.FromHz(sampleRateHz);

        _device.TunerGainMode = TunerGainModes.Manual;
        _device.TunerGain = gainDb;

        _actualFrequencyHz = _device.CenterFrequency.Hz;
        _actualSampleRateHz = _device.SampleRate.Hz;
        _currentGainDb = gainDb;

        _device.UseRawBufferMode = true;
        _device.MaxAsyncBufferSize = 512 * 1024;
        _device.DropSamplesOnFullBuffer = true;

        _device.ResetDeviceBuffer();

        ulong bytesNeeded = (ulong)sampleCount * 2UL;
        var output = new byte[bytesNeeded];
        ulong writePos = 0;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Create a named handler so we can unsubscribe
        EventHandler<SamplesAvailableEventArgs>? handler = null;

        handler = (sender, args) =>
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

        _device.SamplesAvailable += handler;

        try
        {
            _device.StartReadSamplesAsync();

            var dwellTimeout = TimeSpan.FromSeconds((double)sampleCount / _actualSampleRateHz + 5.0);

            await tcs.Task.WaitAsync(dwellTimeout, ct);

            return output;
        }
        finally
        {
            try { _device.StopReadSamplesAsync(); } catch { }

            // IMPORTANT: remove handler
            _device.SamplesAvailable -= handler;
        }
    }


    // ---------------------------------------------------------
    // Spectrum capture
    // ---------------------------------------------------------
    public async Task<double[]> CaptureSpectrumAsync(
        double frequencyHz,
        double sampleRateHz,
        double gainDb,
        TimeSpan dwellTime,
        int fftSize,
        IFftEngine fftEngine,
        CancellationToken ct)
    {
        // Compute sample count safely
        uint sampleCount = (uint)Math.Ceiling(sampleRateHz * dwellTime.TotalSeconds);

        // Capture raw IQ using your existing async pipeline
        var rawIq = await CaptureRawIqAsync(
            frequencyHz,
            sampleRateHz,
            gainDb,
            sampleCount,
            ct).ConfigureAwait(false);

        // Use your FFT engine to compute a spectrum
        double[] spectrum = fftEngine.ComputeSpectrum(rawIq, fftSize);

        return spectrum;
    }

    // ---------------------------------------------------------
    // Bias Tee / Direct Sampling
    // ---------------------------------------------------------
    public void SetBiasTee(bool enabled)
    {
        if (_device is null)
            throw new InvalidOperationException("SDR not initialized.");

        _device.SetBiasTee(enabled ? BiasTeeModes.Enabled : BiasTeeModes.Disabled);
    }

    public void SetPpmCorrection(int ppm) { }
    public void SetDirectSampling(bool enabled) { }

    // ---------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------
    public void Dispose()
    {
        if (_device != null)
        {
            try
            {
                var manager = RtlSdrDeviceManager.Instance;
                manager.CloseManagedDevice("rasta-persistent");
            }
            catch { }

            _device = null;
        }
    }
}
