using CommunityToolkit.Mvvm.ComponentModel;

namespace RASTA.Core.Telescope
{
    public partial class TelescopeState: ObservableObject
    {
        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private double rightAscensionHours;

        [ObservableProperty]
        private double declinationDeg;

        [ObservableProperty]    
        private double azimuthDeg;
        [ObservableProperty]
        private double elevationDeg ;

        [ObservableProperty]
        private double siteLatitudeDeg;
        
        [ObservableProperty]
        private double siteLongitudeDeg;
        
        [ObservableProperty]
        private double siteElevationM;
        
        [ObservableProperty]
        private CoordinateMode mode;

        [ObservableProperty]
        private bool trackingEnabled;

        [ObservableProperty]
        private int trackingRate;

        [ObservableProperty]
        private bool isSlewing;

        [ObservableProperty]
        private bool isParked;

        [ObservableProperty]
        private bool isParking;

        public bool WasParkedOnConnect { get; set; }

        [ObservableProperty]
        private bool isHome;

    }

}
