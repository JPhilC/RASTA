using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Telescope;

namespace RASTA.App.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        // Auto = use ASCOM AlignmentMode when connected
        // Manual = user chooses explicitly
        [ObservableProperty]
        private bool autoCoordinateMode = true;

        [ObservableProperty]
        private CoordinateMode coordinateMode = CoordinateMode.Equatorial;

        public void SetCoordinateMode(CoordinateMode mode)
        {
            AutoCoordinateMode = false;
            CoordinateMode = mode;
        }

        public void SetAutoMode()
        {
            AutoCoordinateMode = true;
        }

        // Called when telescope connects
        public void ApplyHardwareAlignmentMode(CoordinateMode hardwareMode)
        {
            if (AutoCoordinateMode)
                CoordinateMode = hardwareMode;
        }
    }
}
