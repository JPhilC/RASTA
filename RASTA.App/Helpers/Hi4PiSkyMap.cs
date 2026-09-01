using System;
using System.IO;
using System.Reflection;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// Loads the embedded, pre-downgraded HI4PI all-sky HI column-density (N_HI) grid and samples
    /// it by Galactic longitude/latitude for MilkyWayBackgroundBuilder.
    ///
    /// Source: HI4PI Collaboration (2016), A&amp;A 594, A116 - a 21cm all-sky survey combining the
    /// Effelsberg-Bonn HI Survey and the Galactic All-Sky Survey, distributed via NASA LAMBDA as a
    /// HEALPix Nside=1024 FITS table (12,582,912 points, ~577 MB). That's far too large/fine to
    /// ship with the app or fetch at runtime (and Plan needs to stay usable fully offline - see
    /// CLAUDE.md "Navigation"), so it's downgraded once, offline, to a plain 720x360 (0.5-degree)
    /// Galactic lon/lat grid - a straight per-cell average of the source table's own explicit
    /// GLON/GLAT/NHI columns (no HEALPix ring/nest pixel math needed for that). ~1 MB, embedded as
    /// a resource rather than fetched live. Half a degree is still coarser than the 0.27-degree
    /// native survey resolution, deliberately so - this is background/orientation context for the
    /// sky map, not survey-grade imaging - but resolves noticeably more real structure than the
    /// original 1-degree cut, which is what this replaced.
    ///
    /// Per-attribution terms of use: "Permission is granted for publication and reproduction of
    /// this material for scientific and educational purposes" - citation of the HI4PI publication
    /// and the required Parkes/Effelsberg acknowledgement belong wherever this data is credited
    /// (About/Help), not repeated here.
    ///
    /// Binary format (see scripts/ - the one-off Python downgrade itself isn't part of this build):
    /// two little-endian int32s (nlon, nlat), then nlon*nlat little-endian float32 cell values,
    /// row-major lat-outer/lon-inner. Cell (lonIdx, latIdx) covers lon [lonIdx, lonIdx+1)*0.5
    /// degrees (0-360, west to east) and lat [latIdx, latIdx+1)*0.5 - 90 degrees (-90 to 90, south
    /// to north).
    /// </summary>
    internal static class Hi4PiSkyMap
    {
        private const string ResourceName = "RASTA.App.Resources.Data.hi4pi_nhi_grid.bin";

        private static readonly object Lock = new();
        private static volatile float[]? _grid; // [latIdx * _nlon + lonIdx], N_HI in cm^-2
        private static int _nlon;
        private static int _nlat;

        // log10(N_HI) normalization range, computed once at load time as the 1st/99th percentile
        // across the grid's own cells (the same "robust range from actual data" approach
        // SpectrumViewModel.ApplyRobustYAxisRange uses) - not a fixed constant, so a future
        // regeneration of the grid (different resolution, a newer survey) re-derives its own range
        // automatically rather than needing a hand-tuned number kept in sync by hand.
        private static double _logMin;
        private static double _logMax;

        private static void EnsureLoaded()
        {
            if (_grid != null)
                return;

            lock (Lock)
            {
                if (_grid != null)
                    return;

                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
                using var reader = new BinaryReader(stream);

                int nlon = reader.ReadInt32();
                int nlat = reader.ReadInt32();
                var grid = new float[nlon * nlat];
                for (int i = 0; i < grid.Length; i++)
                    grid[i] = reader.ReadSingle();

                var logValues = new double[grid.Length];
                for (int i = 0; i < grid.Length; i++)
                    logValues[i] = Math.Log10(Math.Max(grid[i], 1e10)); // guard against a stray non-positive cell
                Array.Sort(logValues);

                _logMin = Percentile(logValues, 0.01);
                _logMax = Percentile(logValues, 0.99);

                _nlon = nlon;
                _nlat = nlat;
                _grid = grid; // publish last - EnsureLoaded's null check above is what other threads gate on
            }
        }

        private static double Percentile(double[] sortedValues, double fraction)
        {
            double pos = fraction * (sortedValues.Length - 1);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sortedValues[lo];
            double frac = pos - lo;
            return sortedValues[lo] * (1 - frac) + sortedValues[hi] * frac;
        }

        /// <summary>
        /// Bilinearly-sampled N_HI (cm^-2) at the given Galactic longitude/latitude. Wraps in
        /// longitude (the grid covers the full 0-360 circle); clamps at the latitude poles.
        /// </summary>
        public static double SampleNhi(double lDeg, double bDeg)
        {
            EnsureLoaded();
            var grid = _grid!;

            double lon = ((lDeg % 360.0) + 360.0) % 360.0;
            double lat = Math.Clamp(bDeg, -90.0, 90.0 - 1e-6);

            double lonCell = lon / 360.0 * _nlon - 0.5;
            double latCell = (lat + 90.0) / 180.0 * _nlat - 0.5;

            int lon0 = (int)Math.Floor(lonCell);
            int lat0 = (int)Math.Floor(latCell);
            double tLon = lonCell - lon0;
            double tLat = latCell - lat0;

            int lon0Wrapped = ((lon0 % _nlon) + _nlon) % _nlon;
            int lon1Wrapped = (lon0Wrapped + 1) % _nlon;
            int lat0Clamped = Math.Clamp(lat0, 0, _nlat - 1);
            int lat1Clamped = Math.Clamp(lat0 + 1, 0, _nlat - 1);

            double v00 = grid[lat0Clamped * _nlon + lon0Wrapped];
            double v10 = grid[lat0Clamped * _nlon + lon1Wrapped];
            double v01 = grid[lat1Clamped * _nlon + lon0Wrapped];
            double v11 = grid[lat1Clamped * _nlon + lon1Wrapped];

            double v0 = v00 * (1 - tLon) + v10 * tLon;
            double v1 = v01 * (1 - tLon) + v11 * tLon;
            return v0 * (1 - tLat) + v1 * tLat;
        }

        /// <summary>
        /// N_HI at (lDeg, bDeg), normalized to [0, 1] via the grid's own 1st/99th log10 percentile
        /// range - the same shape MilkyWayBackgroundBuilder's earlier analytic brightness returned,
        /// now driven by real survey data instead of a Gaussian approximation.
        /// </summary>
        public static double SampleBrightness(double lDeg, double bDeg)
        {
            double nhi = SampleNhi(lDeg, bDeg);
            double logNhi = Math.Log10(Math.Max(nhi, 1e10));
            double t = (logNhi - _logMin) / (_logMax - _logMin);
            return Math.Clamp(t, 0.0, 1.0);
        }
    }
}
