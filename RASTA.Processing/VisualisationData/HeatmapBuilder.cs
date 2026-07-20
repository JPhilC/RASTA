using RASTA.Core.Capture;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RASTA.Processing.VisualisationData
{
    public class HeatmapBuilder
    {
        // Existing overload: from observations
        public BitmapSource BuildHeatmapImage(IEnumerable<ObservationRecord> records)
        {
            var list = records.ToList();
            if (list.Count == 0)
                throw new InvalidOperationException("No observations provided.");

            double minAz = list.Min(r => r.Metadata.Pointing.AzimuthDeg);
            double maxAz = list.Max(r => r.Metadata.Pointing.AzimuthDeg);
            double minEl = list.Min(r => r.Metadata.Pointing.ElevationDeg);
            double maxEl = list.Max(r => r.Metadata.Pointing.ElevationDeg);

            int width = 600;
            int height = 600;

            byte[] pixels = new byte[width * height * 4];

            double maxIntensity = list.Max(r => r.AveragedSpectrum.Max());

            foreach (var record in list)
            {
                double az = record.Metadata.Pointing.AzimuthDeg;
                double el = record.Metadata.Pointing.ElevationDeg;

                int x = (int)((az - minAz) / (maxAz - minAz) * (width - 1));
                int y = height - 1 - (int)((el - minEl) / (maxEl - minEl) * (height - 1));

                double intensity = record.AveragedSpectrum.Max();
                double value = intensity / maxIntensity;

                byte r = (byte)(value * 255);
                byte g = 50;
                byte b = (byte)(255 - r);

                int index = (y * width + x) * 4;
                pixels[index + 0] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = 255;
            }

            return BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
        }

        // New overload: from intensity grid
        public BitmapSource BuildHeatmapImage(double[,] intensityGrid)
        {
            int width = intensityGrid.GetLength(0);
            int height = intensityGrid.GetLength(1);

            byte[] pixels = new byte[width * height * 4];

            double max = double.MinValue;
            double min = double.MaxValue;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    double v = intensityGrid[x, y];
                    if (v > max) max = v;
                    if (v < min) min = v;
                }
            }

            double range = max - min;
            if (range <= 0) range = 1;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    double value = (intensityGrid[x, y] - min) / range;

                    byte r = (byte)(value * 255);
                    byte g = 50;
                    byte b = (byte)(255 - r);

                    int index = (y * width + x) * 4;
                    pixels[index + 0] = b;
                    pixels[index + 1] = g;
                    pixels[index + 2] = r;
                    pixels[index + 3] = 255;
                }
            }

            return BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
        }
    }
}
