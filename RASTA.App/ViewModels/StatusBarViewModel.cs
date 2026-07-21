using CommunityToolkit.Mvvm.ComponentModel;

namespace RASTA.App.ViewModels
{
    public partial class StatusBarViewModel : ObservableObject
    {
        [ObservableProperty]
        private string telescopeStatus = "Disconnected";

        [ObservableProperty]
        private string coordinateStatus = "RA: --  Dec: --";

        [ObservableProperty]
        private string captureStatus = "Idle";

        public void UpdateEquatorial(double raHours, double decDeg)
        {
            CoordinateStatus = $"RA: {raHours:F2}h  Dec: {decDeg:F2}°";
        }

        public void UpdateHorizontal(double azDeg, double altDeg)
        {
            CoordinateStatus = $"Az: {azDeg:F1}°  Alt: {altDeg:F1}°";
        }

        public void UpdateTelescopeStatus(string status) =>
            TelescopeStatus = status;

        public void UpdateCaptureStatus(string status) =>
            CaptureStatus = status;
    }
}
