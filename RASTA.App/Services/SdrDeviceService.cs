using RASTA.Core.Sdr;
using RASTA.Infrastructure.Logging;
using RtlSdrManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RASTA.App.Services
{
    public class SdrDeviceService
    {
        private readonly SdrState _state;
        private readonly RastaLogger _logger;

        public SdrDeviceService(SdrState state, RastaLogger logger)
        {
            _state = state;
            _logger = logger;
        }

        public async Task EnumerateDevicesAsync()
        {
            await Task.Run(async () =>
            {
                var manager = RtlSdrDeviceManager.Instance;

                // RTL-SDR driver needs time to initialise after plug-in
                const int maxAttempts = 10;
                const int delayMs = 250;

                bool refreshSucceeded = false;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        manager.RefreshDevices();   // <-- this is the call that fails
                        refreshSucceeded = true;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(delayMs);  // wait and retry
                    }
                }

                if (!refreshSucceeded)
                {
                    HandleDeviceRemoved();
                    _logger.Warn("SDR enumeration failed: driver not ready after retries.");
                    return;
                }

                int count = manager.CountDevices;

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
                        Index = index,  // now uint, correct
                        Manufacturer = info.Manufacturer,
                        Product = info.ProductType,
                        Serial = info.Serial
                    });
                }

                _state.Devices = list;
                _state.IsConnected = true;

                if (_state.SelectedDevice is null)
                    _state.SelectedDevice = list.First();
            });
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

            _logger.Warn("SDR device removed.");
        }

        public void LoadCapabilities()
        {
            if (_state.SelectedDevice == null)
                return;

            try
            {
                var manager = RtlSdrDeviceManager.Instance;
                manager.OpenManagedDevice(_state.SelectedDevice.Index, "probe");

                var dev = manager["probe"];

                _state.SupportedGains = dev.SupportedTunerGains.ToList();
                _state.TunerType = dev.TunerType.ToString();

                manager.CloseManagedDevice("probe");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load SDR capabilities: {ex.Message}");
            }
        }
    }
}
