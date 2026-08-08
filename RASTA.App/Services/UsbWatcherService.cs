using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace RASTA.App.Services
{
    public class UsbWatcherService : IDisposable
    {
        private readonly SdrDeviceService _sdrService;
        private HwndSource? _source;

        private readonly Timer _debounceTimer;
        private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(250);

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        public UsbWatcherService(SdrDeviceService sdrService)
        {
            _sdrService = sdrService;

            // async void timer callback - must not let anything escape. An exception
            // thrown here would be running on a raw ThreadPool thread with no await
            // boundary for a caller to catch, which crashes the whole process (see
            // App.OnAppDomainUnhandledException, which is the last line of defence for
            // anything that still gets through despite this).
            _debounceTimer = new Timer(async _ =>
            {
                try
                {
                    await _sdrService.EnumerateDevicesAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"USB device enumeration failed: {ex.Message}");
                }
            });

            var window = Application.Current.MainWindow;
            var helper = new WindowInteropHelper(window);

            _source = HwndSource.FromHwnd(helper.Handle);
            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                System.Diagnostics.Debug.WriteLine($"Device change event: {eventType}");

                if (eventType == DBT_DEVICEARRIVAL ||
                    eventType == DBT_DEVNODES_CHANGED ||
                    eventType == DBT_DEVICEREMOVECOMPLETE)
                {
                    // Reset debounce timer
                    _debounceTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _source?.RemoveHook(WndProc);
            _debounceTimer?.Dispose();
        }
    }
}
