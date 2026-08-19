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

        // Common - the true angular separation (great-circle degrees) wanted between
        // adjacent dwell points on the sky, not a raw per-axis coordinate step. SweepPlanner
        // derives each axis's actual coordinate step from this: Dec/Elevation use it directly,
        // while RA/Azimuth are corrected by cos(Dec)/cos(Elevation) per row so the resulting
        // grid is (approximately) equally spaced on the celestial sphere rather than equally
        // spaced in RA-hours/Az-degrees, which shrinks in real angle away from the equator/horizon.
        [ObservableProperty] private double angularSeparationDeg;

        // Optional dwell time per point
        [ObservableProperty] private TimeSpan dwellTime = TimeSpan.FromSeconds(1);

        public bool IsEquatorial => Mode == CoordinateMode.Equatorial;
        public bool IsAltAz => Mode == CoordinateMode.AltAz;

        public override string ToString()
        {
            return Mode == CoordinateMode.AltAz
                ? $"AltAz Range: Az {AzimuthStartDeg}→{AzimuthEndDeg}, El {AltitudeStartDeg}→{AltitudeEndDeg}, Separation {AngularSeparationDeg}°"
                : $"Equatorial Range: RA {RAStartHours}→{RAEndHours}h, Dec {DecStartDeg}→{DecEndDeg}°, Separation {AngularSeparationDeg}°";
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
                AngularSeparationDeg = this.AngularSeparationDeg,
            };
        }

    }

}
