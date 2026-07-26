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

        public TargetPoint(CoordinateMode mode,
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

        public static TargetPoint FromRaDec(double raHours, double decDeg)
            => new TargetPoint(CoordinateMode.Equatorial, raHours, decDeg, double.NaN, double.NaN);

        public static TargetPoint FromAzEl(double azDeg, double elDeg)
            => new TargetPoint(CoordinateMode.AltAz, double.NaN, double.NaN, azDeg, elDeg);
    }

}
