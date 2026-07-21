using System;
using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Telescope;

namespace RASTA.Core.Planning
{
    public partial class TargetRange : ObservableObject
    {
        // Mode (Equatorial or AltAz)
        [ObservableProperty]
        private CoordinateMode mode;

        // Equatorial
        [ObservableProperty]
        private double rAStartHours;

        [ObservableProperty]
        private double rAEndHours;

        [ObservableProperty]
        private double decStartDeg;

        [ObservableProperty]
        private double decEndDeg;

        // Horizontal
        [ObservableProperty]
        private double azimuthStartDeg;

        [ObservableProperty]
        private double azimuthEndDeg;

        [ObservableProperty]
        private double altitudeStartDeg;

        [ObservableProperty]
        private double altitudeEndDeg;

        // Common
        [ObservableProperty]
        private double stepDeg;

        // Optional dwell time per point
        [ObservableProperty]
        private TimeSpan dwellTime = TimeSpan.FromSeconds(1);

        public override string ToString()
        {
            return mode == CoordinateMode.AltAz
                ? $"AltAz Range: Az {azimuthStartDeg}→{azimuthEndDeg}, El {altitudeStartDeg}→{altitudeEndDeg}, Step {stepDeg}°"
                : $"Equatorial Range: RA {rAStartHours}→{rAEndHours}h, Dec {decStartDeg}→{decEndDeg}°, Step {stepDeg}°";
        }
    }
}
