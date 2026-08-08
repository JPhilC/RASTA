using System;
using System.Collections.Generic;
using System.Linq;
using RASTA.Core.Telescope;
using RASTA.Processing.Mosaic;

namespace RASTA.Processing.Gridding
{
    /// <summary>
    /// Bins a mosaic session's MosaicPosition list onto a uniform RA/Dec or Az/El grid of
    /// LineStrengthDb - the sky-mosaic heatmap and the 3D surface both render this same grid
    /// (one flat, one as a height field). Reworked from the original ObservationRecord-based
    /// version (see CLAUDE.md's "Known incomplete / placeholder areas") to consume real
    /// HiStreamingPipeline output via MosaicProcessor instead of raw AveragedSpectrum.Max().
    /// </summary>
    public class GridBuilder
    {
        public class MosaicGridResult
        {
            /// <summary>
            /// [x, y] average LineStrengthDb of every position that landed in that cell, or
            /// double.NaN for a cell no position landed in (or where every position that did
            /// land there had no usable line peak - see MosaicProcessor.ComputeLineStrengthDb).
            /// Callers should skip/distinguish NaN cells rather than rendering them as 0 dB.
            /// </summary>
            public double[,] IntensityGrid { get; set; } = default!;

            /// <summary>Cell-center coordinate for each column/row - RA hours or Az degrees.</summary>
            public double[] AxisXCenters { get; set; } = default!;

            /// <summary>Cell-center coordinate for each column/row - Dec degrees or El degrees.</summary>
            public double[] AxisYCenters { get; set; } = default!;

            public CoordinateMode Mode { get; set; }
        }

        /// <summary>
        /// Bins each position's pointing (RA/Dec, or Az/El if the session was AltAz) onto a
        /// grid covering the FULL sky (RA 0-24h x Dec -90..+90, or Az 0-360 deg x El 0-90 deg)
        /// at a fixed angular cell size, rather than stretching just the captured area's own
        /// bounding box to fill the image. This is deliberate: a mosaic session only ever
        /// captures a handful of positions at a time, and the intent is to build up the same
        /// full-sky canvas across many sessions over time - a bounding-box-relative grid would
        /// redraw a different, incompatible scale every time, and would visually exaggerate how
        /// much sky a small cluster of nearby positions actually covers. Cells no position
        /// landed in stay NaN (see MosaicGridResult.IntensityGrid), so the rendered map reads
        /// as "sky covered so far", growing as more sessions are processed.
        /// </summary>
        public MosaicGridResult BuildGrid(IEnumerable<MosaicPosition> positions, double cellSizeDeg)
        {
            var list = positions.ToList();
            if (list.Count == 0)
                throw new InvalidOperationException("No positions provided.");
            if (cellSizeDeg <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellSizeDeg), "Cell size must be positive.");

            var mode = list[0].Mode;

            // Full-sky bounds and per-axis cell size, in the axis's own native unit (RA in
            // hours, everything else in degrees) - cellSizeDeg is always a sky-angle size, so
            // the RA axis converts it to hours (15 deg/hour) while the others use it directly.
            double minX, maxX, minY, maxY, cellSizeX, cellSizeY;
            if (mode == CoordinateMode.AltAz)
            {
                (minX, maxX) = (0.0, 360.0);   // Azimuth, degrees
                (minY, maxY) = (0.0, 90.0);    // Elevation, degrees (can't observe below horizon)
                cellSizeX = cellSizeDeg;
                cellSizeY = cellSizeDeg;
            }
            else
            {
                (minX, maxX) = (0.0, 24.0);    // RA, hours
                (minY, maxY) = (-90.0, 90.0);  // Dec, degrees
                cellSizeX = cellSizeDeg / 15.0;
                cellSizeY = cellSizeDeg;
            }

            int gridWidth = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellSizeX));
            int gridHeight = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellSizeY));

            var intensityGrid = new double[gridWidth, gridHeight];
            var countGrid = new int[gridWidth, gridHeight];
            var axisXCenters = new double[gridWidth];
            var axisYCenters = new double[gridHeight];

            for (int gx = 0; gx < gridWidth; gx++)
                axisXCenters[gx] = minX + (gx + 0.5) * cellSizeX;
            for (int gy = 0; gy < gridHeight; gy++)
                axisYCenters[gy] = minY + (gy + 0.5) * cellSizeY;

            foreach (var p in list)
            {
                // A position with no usable line peak (see MosaicProcessor.ComputeLineStrengthDb)
                // reports NaN - skip it rather than letting it poison the whole cell's average
                // for every other position that lands there.
                if (double.IsNaN(p.LineStrengthDb))
                    continue;

                double x, y;
                if (mode == CoordinateMode.AltAz)
                {
                    x = p.AzDeg ?? 0;
                    y = p.AltDeg ?? 0;
                }
                else
                {
                    x = p.RaHours ?? 0;
                    y = p.DecDeg ?? 0;
                }

                int gx = Math.Clamp((int)Math.Floor((x - minX) / cellSizeX), 0, gridWidth - 1);
                int gy = Math.Clamp((int)Math.Floor((y - minY) / cellSizeY), 0, gridHeight - 1);

                intensityGrid[gx, gy] += p.LineStrengthDb;
                countGrid[gx, gy]++;
            }

            for (int gx = 0; gx < gridWidth; gx++)
            {
                for (int gy = 0; gy < gridHeight; gy++)
                {
                    intensityGrid[gx, gy] = countGrid[gx, gy] > 0
                        ? intensityGrid[gx, gy] / countGrid[gx, gy]
                        : double.NaN;
                }
            }

            return new MosaicGridResult
            {
                IntensityGrid = intensityGrid,
                AxisXCenters = axisXCenters,
                AxisYCenters = axisYCenters,
                Mode = mode
            };
        }
    }
}
