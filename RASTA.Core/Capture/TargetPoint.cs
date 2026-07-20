using RASTA.Core.Telescope;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RASTA.Core.Capture
{
    public class TargetPoint
    {
        public CoordinateMode Mode { get; set; }

        // Alt/Az
        public double AzimuthDeg { get; set; }
        public double ElevationDeg { get; set; }

        // RA/Dec
        public double RightAscensionHours { get; set; }
        public double DeclinationDeg { get; set; }
    }
}
