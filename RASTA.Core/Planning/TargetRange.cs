using System;
using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Telescope;

namespace RASTA.Core.Planning
{
    public partial class TargetRange : ObservableObject
    {
        [ObservableProperty]
        private CoordinateMode mode;

        // Equatorial
        [ObservableProperty] private double rAStartHours;
        [ObservableProperty] private double rAEndHours;
        [ObservableProperty] private double decStartDeg;
        [ObservableProperty] private double decEndDeg;

        // Horizontal
        [ObservableProperty] private double azimuthStartDeg;
        [ObservableProperty] private double azimuthEndDeg;
        [ObservableProperty] private double altitudeStartDeg;
        [ObservableProperty] private double altitudeEndDeg;

        // Common
        [ObservableProperty] private double stepDeg;

        // Optional dwell time per point
        [ObservableProperty] private TimeSpan dwellTime = TimeSpan.FromSeconds(1);

        public bool IsEquatorial => Mode == CoordinateMode.Equatorial;
        public bool IsAltAz => Mode == CoordinateMode.AltAz;

        public override string ToString()
        {
            return Mode == CoordinateMode.AltAz
                ? $"AltAz Range: Az {AzimuthStartDeg}→{AzimuthEndDeg}, El {AltitudeStartDeg}→{AltitudeEndDeg}, Step {StepDeg}°"
                : $"Equatorial Range: RA {RAStartHours}→{RAEndHours}h, Dec {DecStartDeg}→{DecEndDeg}°, Step {StepDeg}°";
        }

        public TargetRange Clone()
        {
            return new TargetRange
            {
                Mode = this.Mode,

                // Equatorial
                RAStartHours = this.RAStartHours,
                RAEndHours = this.RAEndHours,
                DecStartDeg = this.DecStartDeg,
                DecEndDeg = this.DecEndDeg,

                // Horizontal
                AzimuthStartDeg = this.AzimuthStartDeg,
                AzimuthEndDeg = this.AzimuthEndDeg,
                AltitudeStartDeg = this.AltitudeStartDeg,
                AltitudeEndDeg = this.AltitudeEndDeg,

                // Common
                StepDeg = this.StepDeg,
            };
        }

    }

}
