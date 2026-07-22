using RASTA.Core.Telescope;

namespace RASTA.Core.Capture
{
    public class TargetPoint
    {
        public CoordinateMode Mode { get; }

        // Equatorial
        public double RightAscensionHours { get; }
        public double DeclinationDeg { get; }

        // AltAz
        public double AzimuthDeg { get; }
        public double ElevationDeg { get; }

        private TargetPoint(CoordinateMode mode,
                            double raHours,
                            double decDeg,
                            double azDeg,
                            double elDeg)
        {
            Mode = mode;

            RightAscensionHours = raHours;
            DeclinationDeg = decDeg;

            AzimuthDeg = azDeg;
            ElevationDeg = elDeg;
        }

        // Factory for Equatorial
        public static TargetPoint FromRaDec(CoordinateMode mode, double raHours, double decDeg)
        {
            return new TargetPoint(mode, raHours, decDeg, double.NaN, double.NaN);
        }

        // Factory for AltAz
        public static TargetPoint FromAzEl(CoordinateMode mode, double azDeg, double elDeg)
        {
            return new TargetPoint(mode, double.NaN, double.NaN, azDeg, elDeg);
        }
    }

}
