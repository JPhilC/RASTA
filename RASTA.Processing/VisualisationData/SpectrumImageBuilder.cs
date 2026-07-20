using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RASTA.Core.Capture;

namespace RASTA.Processing.VisualisationData
{
    public class SpectrumImageBuilder
    {
        public BitmapSource BuildSpectrumImage(ObservationRecord record)
        {
            var spectrum = record.AveragedSpectrum;
            int width = spectrum.Length;
            int height = 300;

            // Normalise for display
            double max = spectrum.Max();
            double min = spectrum.Min();
            double range = max - min;

            byte[] pixels = new byte[width * height * 4];

            for (int x = 0; x < width; x++)
            {
                double value = (spectrum[x] - min) / range;
                int y = height - 1 - (int)(value * (height - 1));

                int index = (y * width + x) * 4;

                pixels[index + 0] = 0;               // Blue
                pixels[index + 1] = 255;             // Green
                pixels[index + 2] = 0;               // Red
                pixels[index + 3] = 255;             // Alpha
            }

            var bmp = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);

            return bmp;
        }
    }
}
