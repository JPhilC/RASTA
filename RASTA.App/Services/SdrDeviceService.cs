using RASTA.Core.Sdr;
using RASTA.Infrastructure.Logging;
using RASTA.Infrastructure.Sdr;
using RtlSdrManager;

namespace RASTA.App.Services;

public class SdrDeviceService : IDisposable
{
    private readonly SdrState _state;
    private readonly RastaLogger _logger;

    private RtlSdrDevice? _device;   // persistent device instance

    public SdrDeviceService(SdrState state, RastaLogger logger)
    {
        _state = state;
        _logger = logger;
    }

    // Serializes enumeration. UsbWatcherService's debounce timer and explicit callers can
    // otherwise run two enumerations concurrently (seen in the log as interleaved
    // "Enumerating"/"removed"/"created" lines), with one disposing the device the other
    // just opened.
    private readonly SemaphoreSlim _enumerateLock = new(1, 1);

    public async Task EnumerateDevicesAsync()
    {
        await _enumerateLock.WaitAsync();
        try
        {
            await EnumerateDevicesCoreAsync();
        }
        finally
        {
            _enumerateLock.Release();
        }
    }

    private async Task EnumerateDevicesCoreAsync()
    {
        _logger.Info("Enumerating SDR devices.");
        await Task.Run(async () =>
        {
            var manager = RtlSdrDeviceManager.Instance;

            const int maxAttempts = 10;
            const int delayMs = 250;

            bool refreshSucceeded = false;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    manager.RefreshDevices();
                    refreshSucceeded = true;
                    break;
                }
                catch
                {
                    await Task.Delay(delayMs);
                }
            }

            if (!refreshSucceeded)
            {
                // RefreshDevices throwing is NOT evidence the SDR was unplugged - it
                // reliably fails while the open device is busy streaming (a 2026-11-07
                // Region sweep died this way: an unrelated USB event mid-capture, refresh
                // failed, and the live device was disposed out from under the sweep). A
                // genuine unplug shows up as a successful refresh with the device absent,
                // handled below. If the device really has gone, the capture paths' own
                // stall watchdog (CaptureViewModel.StreamStallTimeout) reports it.
                if (_device != null)
                {
                    _logger.Warn("SDR enumeration failed while a device is open (likely busy streaming) - keeping the existing device.");
                    return;
                }

                HandleDeviceRemoved();
                _logger.Warn("SDR enumeration failed: driver not ready after retries.");
                return;
            }

            int count = manager.Devices.Count;
            if (count == 0)
            {
                HandleDeviceRemoved();
                return;
            }

            var list = new List<SdrDeviceDescriptor>();

            foreach (var (index, info) in manager.Devices)
            {
                list.Add(new SdrDeviceDescriptor
                {
                    Index = index,
                    Manufacturer = info.Manufacturer,
                    Product = info.ProductType,
                    Serial = info.Serial
                });
            }

            System.Diagnostics.Debug.WriteLine($"Found {list.Count} SDR devices.");

            _state.Devices = list;
            _state.IsConnected = true;

            if (_state.SelectedDevice is null)
                _state.SelectedDevice = list.First();

            // If we already have this exact physical device open (matched by serial - stable
            // across a re-enumeration, unlike Index, which can shift if some other USB
            // device's insert/removal reorders the bus) and it's still present in the fresh
            // list, there's nothing to do. This matters a lot now that a calibration run can
            // take minutes (several confirmation prompts plus a real mount slew) - during
            // that whole window, ANY unrelated USB event anywhere on the system (a totally
            // different device attaching, a driver reconfiguring, ...) fires
            // UsbWatcherService's debounce timer and lands back here. Unconditionally tearing
            // down and reopening an already-working, still-present device on every such event
            // was harmless while the calibration flow was quick; now, if it races with an
            // in-progress capture (the gain sweep/baseline capture actively streaming from
            // it), the forced dispose+reopen below can fail - and that failure was being
            // reported (via HandleDeviceRemoved) as the SDR having been unplugged, even
            // though it never was.
            bool sameDeviceStillOpen =
                _device != null &&
                !string.IsNullOrEmpty(_state.SelectedDevice.Serial) &&
                list.Any(d => d.Serial == _state.SelectedDevice.Serial);

            if (sameDeviceStillOpen)
                return;

            CreatePersistentDevice(_state.SelectedDevice.Index);
        });
    }

    /// <summary>
    /// Forcibly closes and reopens the persistent SDR device - used by CaptureViewModel's
    /// stall-retry path when an open device has stopped delivering samples. Unlike
    /// EnumerateDevicesAsync, this deliberately tears down the existing device first (it's
    /// known bad at this point, and releasing its USB claim is what lets RefreshDevices see
    /// it again). Prefers the previously selected device by serial, falling back to the
    /// first one found. Returns true if a working device is open afterwards.
    /// </summary>
    public async Task<bool> ReopenDeviceAsync()
    {
        await _enumerateLock.WaitAsync();
        try
        {
            return await Task.Run(async () =>
            {
                _logger.Warn("Reopening SDR device after a streaming stall.");
                string? previousSerial = _state.SelectedDevice?.Serial;

                DisposeDevice();

                var manager = RtlSdrDeviceManager.Instance;
                bool refreshSucceeded = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        manager.RefreshDevices();
                        refreshSucceeded = true;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(500);
                    }
                }

                if (!refreshSucceeded || manager.Devices.Count == 0)
                {
                    HandleDeviceRemoved();
                    _logger.Warn("SDR reopen failed: device not found.");
                    return false;
                }

                var list = manager.Devices
                    .Select(kv => new SdrDeviceDescriptor
                    {
                        Index = kv.Key,
                        Manufacturer = kv.Value.Manufacturer,
                        Product = kv.Value.ProductType,
                        Serial = kv.Value.Serial
                    })
                    .ToList();

                var chosen = list.FirstOrDefault(d => !string.IsNullOrEmpty(previousSerial) && d.Serial == previousSerial)
                             ?? list.First();

                _state.Devices = list;
                _state.SelectedDevice = chosen;
                _state.IsConnected = true;

                CreatePersistentDevice(chosen.Index);
                return _device != null;
            });
        }
        finally
        {
            _enumerateLock.Release();
        }
    }

    private void CreatePersistentDevice(uint deviceIndex)
    {
        try
        {
            DisposeDevice();

            var manager = RtlSdrDeviceManager.Instance;

            // Open the device here (NOT in RtlSdrDevice)
            manager.OpenManagedDevice(deviceIndex, "rasta-persistent");
            var dev = manager["rasta-persistent"];

            _device = new RtlSdrDevice(deviceIndex);
            _device.AttachManagedDevice(dev);

            _logger.Info($"Persistent SDR device created for index {deviceIndex}.");

            LoadCapabilities(dev);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to create persistent SDR device: {ex.Message}");
            HandleDeviceRemoved();
        }
    }

    private void LoadCapabilities(RtlSdrManagedDevice dev)
    {
        try
        {
            _state.SupportedGains = dev.SupportedTunerGains.ToList();
            _state.TunerType = dev.TunerType.ToString();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load SDR capabilities: {ex.Message}");
        }
    }

    public ISdrDevice? GetDevice() => _device;

    private void DisposeDevice()
    {
        try
        {
            _device?.Dispose();
        }
        catch { }

        _device = null;
    }

    public void HandleDeviceRemoved()
    {
        _state.IsConnected = false;
        _state.Devices = null;
        _state.SelectedDevice = null;
        _state.SupportedGains = null;
        _state.TunerType = null;
        _state.ActualFrequencyHz = null;
        _state.ActualSampleRateHz = null;
        _state.ActualGainDb = null;

        DisposeDevice();

        _logger.Warn("SDR device removed.");
    }

    public void Dispose()
    {
        DisposeDevice();
    }
}
