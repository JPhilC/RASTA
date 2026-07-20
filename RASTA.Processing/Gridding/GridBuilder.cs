using System;
using System.Collections.Generic;
using System.Linq;
using RASTA.Core.Capture;
using RASTA.Core.Telescope;

namespace RASTA.Processing.Gridding
{
    public class GridBuilder
    {
        public class GridResult
        {
            public double[,] IntensityGrid { get; set; } = default!;
            public double[,] AzimuthGrid { get; set; } = default!;
            public double[,] ElevationGrid { get; set; } = default!;
            public double[,] RaGrid { get; set; } = default!;
            public double[,] DecGrid { get; set; } = default!;
        }

        public GridResult BuildGrid(
            IEnumerable<ObservationRecord> records,
            int gridWidth,
            int gridHeight)
        {
            var list = records.ToList();
            if (list.Count == 0)
                throw new InvalidOperationException("No observations provided.");

            var mode = list[0].Metadata.Pointing.Mode;

            // Determine bounds
            double minX, maxX, minY, maxY;

            if (mode == CoordinateMode.AltAz)
            {
                minX = list.Min(r => r.Metadata.Pointing.AzimuthDeg);
                maxX = list.Max(r => r.Metadata.Pointing.AzimuthDeg);
                minY = list.Min(r => r.Metadata.Pointing.ElevationDeg);
                maxY = list.Max(r => r.Metadata.Pointing.ElevationDeg);
            }
            else
            {
                minX = list.Min(r => r.Metadata.Pointing.RightAscensionHours);
                maxX = list.Max(r => r.Metadata.Pointing.RightAscensionHours);
                minY = list.Min(r => r.Metadata.Pointing.DeclinationDeg);
                maxY = list.Max(r => r.Metadata.Pointing.DeclinationDeg);
            }

            // Allocate grids
            var intensityGrid = new double[gridWidth, gridHeight];
            var countGrid = new int[gridWidth, gridHeight];

            var azGrid = new double[gridWidth, gridHeight];
            var elGrid = new double[gridWidth, gridHeight];
            var raGrid = new double[gridWidth, gridHeight];
            var decGrid = new double[gridWidth, gridHeight];

            // Fill coordinate grids
            for (int gx = 0; gx < gridWidth; gx++)
            {
                for (int gy = 0; gy < gridHeight; gy++)
                {
                    double x = minX + (gx / (double)(gridWidth - 1)) * (maxX - minX);
                    double y = minY + (gy / (double)(gridHeight - 1)) * (maxY - minY);

                    if (mode == CoordinateMode.AltAz)
                    {
                        azGrid[gx, gy] = x;
                        elGrid[gx, gy] = y;
                    }
                    else
                    {
                        raGrid[gx, gy] = x;
                        decGrid[gx, gy] = y;
                    }
                }
            }

            // Map observations to grid
            foreach (var record in list)
            {
                double x, y;

                if (mode == CoordinateMode.AltAz)
                {
                    x = record.Metadata.Pointing.AzimuthDeg;
                    y = record.Metadata.Pointing.ElevationDeg;
                }
                else
                {
                    x = record.Metadata.Pointing.RightAscensionHours;
                    y = record.Metadata.Pointing.DeclinationDeg;
                }

                int gx = (int)((x - minX) / (maxX - minX) * (gridWidth - 1));
                int gy = (int)((y - minY) / (maxY - minY) * (gridHeight - 1));

                gx = Math.Clamp(gx, 0, gridWidth - 1);
                gy = Math.Clamp(gy, 0, gridHeight - 1);

                double intensity = record.AveragedSpectrum.Max();

                intensityGrid[gx, gy] += intensity;
                countGrid[gx, gy]++;
            }

            // Average intensities
            for (int gx = 0; gx < gridWidth; gx++)
            {
                for (int gy = 0; gy < gridHeight; gy++)
                {
                    if (countGrid[gx, gy] > 0)
                        intensityGrid[gx, gy] /= countGrid[gx, gy];
                }
            }

            return new GridResult
            {
                IntensityGrid = intensityGrid,
                AzimuthGrid = azGrid,
                ElevationGrid = elGrid,
                RaGrid = raGrid,
                DecGrid = decGrid
            };
        }
    }
}
