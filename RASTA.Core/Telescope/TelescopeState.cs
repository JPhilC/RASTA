using System;
using System.Collections.Generic;
using System.Text;

namespace RASTA.Core.Telescope
{
    public class TelescopeState
    {
        public bool IsConnected { get; set; }

        public double RightAscensionHours { get; set; }
        public double DeclinationDeg { get; set; }

        public double AzimuthDeg { get; set; }
        public double ElevationDeg  { get; set; }

        public double SiteLatitudeDeg { get; set; }
        public double SiteLongitudeDeg { get; set; }
        public double SiteElevationM { get; set; }

        public CoordinateMode Mode { get; set; }

        public bool TrackingEnabled { get; set; }
        public int TrackingRate { get; set; }

        public bool IsSlewing { get; set; }
        public bool IsParked { get; set; }

        public bool IsParking { get; set; }

        public bool WasParkedOnConnect { get; set; }

        public bool IsHome { get; set; }

    }

}
