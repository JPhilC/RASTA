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

    public async Task EnumerateDevicesAsync()
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
