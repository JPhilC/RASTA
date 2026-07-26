using RtlSdrManager;
using System;
using System.Windows;
using System.Windows.Interop;

namespace RASTA.App.Services
{
    public class UsbWatcherService : IDisposable
    {
        private readonly SdrDeviceService _sdrService;
        private HwndSource? _source;

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        public UsbWatcherService(SdrDeviceService sdrService)
        {
            _sdrService = sdrService;

            var window = Application.Current.MainWindow;
            var helper = new WindowInteropHelper(window);

            _source = HwndSource.FromHwnd(helper.Handle);
            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVICEARRIVAL = 0x8000;
            const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
            const int DBT_DEVNODES_CHANGED = 0x0007;

            if (msg == WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();

                if (eventType == DBT_DEVICEARRIVAL ||
                    eventType == DBT_DEVNODES_CHANGED ||
                    eventType == DBT_DEVICEREMOVECOMPLETE)
                {
                    _ = _sdrService.EnumerateDevicesAsync();
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _source?.RemoveHook(WndProc);
        }
    }
}
