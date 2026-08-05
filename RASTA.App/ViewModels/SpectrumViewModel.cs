using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Configuration;

namespace RASTA.App.ViewModels
{
    public enum SpectrumMode
    {
        IF,
        HiFrequency,
        HiVelocity,
        TTRT
    }

    public partial class SpectrumViewModel : ObservableObject
    {
        private ObservablePoint[] points;

        [ObservableProperty]
        private SpectrumMode mode = SpectrumMode.HiFrequency;


        // FFT bin values (Y-axis)
        private double[]? liveSpectrum;

        // X-axis values (frequency or velocity)
        private double[]? xAxis;

        private double[]? frequencies;

        public ISeries[] Series { get; private set; }
        public Axis[] XAxes { get; private set; }
        public Axis[] YAxes { get; private set; }

        // Backing fields
        private int _fftSize;
        public int FftSize
        {
            get => _fftSize;
            private set
            {
                if (_fftSize != value)
                {
                    _fftSize = value;
                    OnPropertyChanged(nameof(FftSize));
                }
            }
        }

        private double _centerFreqHz;
        public double CenterFreqHz
        {
            get => _centerFreqHz;
            private set
            {
                if (_centerFreqHz != value)
                {
                    _centerFreqHz = value;
                    OnPropertyChanged(nameof(CenterFreqHz));
                }
            }
        }

        private double _samplingFrequencyHz;
        public double SamplingFrequencyHz
        {
            get => _samplingFrequencyHz;
            private set
            {
                if (_samplingFrequencyHz != value)
                {
                    _samplingFrequencyHz = value;
                    OnPropertyChanged(nameof(SamplingFrequencyHz));
                }
            }
        }

        public SpectrumViewModel(int fftSize, double centerFreqHz, double sampleRateHz)
        {
            _fftSize = fftSize;
            _centerFreqHz = centerFreqHz;
            _samplingFrequencyHz = sampleRateHz;


            Series = new ISeries[]
                {
                    new LineSeries<ObservablePoint>
                    {
                        Values = points,
                        Fill = null,
                        GeometrySize = 0,
                        Stroke = new SolidColorPaint(new SKColor(0, 200, 255))
                        {
                            StrokeThickness = 1
                        },
                        LineSmoothness = 0
                    }
                };

            BuildFrequencyAxis();   // Populates liveSpectrum and frequencies arrays

            xAxis = frequencies;

            XAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Frequency",
                    LabelsRotation = 45,
                    MinLimit = frequencies.First(),
                    MaxLimit = frequencies.Last(),
                    Labeler = value => $"{value / 1_000_000.0:F2} MHz",  // Convert to MHz with 2 decimals,
                    MinStep = 100_000, // 100 kHz step
                }
            };

            YAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Power",
                    MinLimit = double.NaN,
                    MaxLimit = double.NaN
                }
            };
        }

        private void ApplyAxisMode()
        {
            switch (Mode)
            {
                case SpectrumMode.IF:
                    XAxes[0].Name = "Frequency (MHz)";
                    XAxes[0].Labeler = value => $"{value / 1_000_000.0:F2} MHz";
                    XAxes[0].MinLimit = frequencies.First();
                    XAxes[0].MaxLimit = frequencies.Last();
                    XAxes[0].MinStep = ComputeBinStep(frequencies);
                    YAxes[0].Name = "Power";
                    break;

                case SpectrumMode.HiFrequency:
                    case SpectrumMode.TTRT:
                    XAxes[0].Name = "Frequency (MHz)";
                    XAxes[0].Labeler = value => $"{value / 1_000_000.0:F2} MHz";
                    XAxes[0].MinLimit = xAxis.First();
                    XAxes[0].MaxLimit = xAxis.Last();
                    XAxes[0].MinStep = ComputeBinStep(xAxis);
                    YAxes[0].Name = "Intensity [a.u.]";
                    break;

                case SpectrumMode.HiVelocity:
                    XAxes[0].Name = "Velocity (km/s)";
                    XAxes[0].Labeler = value => $"{value:F1} km/s";
                    XAxes[0].MinLimit = xAxis.First();
                    XAxes[0].MaxLimit = xAxis.Last();
                    XAxes[0].MinStep = ComputeBinStep(xAxis);
                    YAxes[0].Name = "Intensity [a.u.]";
                    break;
            }
        }

        /// <summary>
        /// One bin's worth of spacing along the given axis - used as MinStep so LiveCharts
        /// can keep subdividing ticks as the user zooms in (its default auto-step behaviour),
        /// without ever showing ticks finer than the data actually resolves.
        /// </summary>
        private static double ComputeBinStep(double[] axis) =>
            axis.Length > 1 ? Math.Abs(axis[^1] - axis[0]) / (axis.Length - 1) : 0;


        // ---------------------------------------------------------
        // PUBLIC API — called by ObserveViewModel
        // ---------------------------------------------------------
        public void UpdateParameters(int fftSize, double centerFreqHz, double sampleRateHz)
        {
            FftSize = fftSize;
            CenterFreqHz = centerFreqHz;
            SamplingFrequencyHz = sampleRateHz;

            // 1. Rebuild frequency axis
            BuildFrequencyAxis();

            // 3. Update axis limits (do NOT replace axes)
            XAxes[0].MinLimit = frequencies.First();
            XAxes[0].MaxLimit = frequencies.Last();
            XAxes[0].Labeler = value => $"{value / 1_000_000.0:F2} MHz";  // Convert to MHz with 2 decimals
            XAxes[0].MinStep = ComputeBinStep(frequencies); // never subdivide finer than one FFT bin

            YAxes[0].MinLimit = -50d;
            YAxes[0].MaxLimit = 50d;

        }


        private DateTime lastUpdateTime = DateTime.MinValue;


        public void UpdateSpectrum(double[] newSpectrum, double[]? newXAxis = null)
        {
            if (DateTime.Now - lastUpdateTime < TimeSpan.FromMilliseconds(50))
                return;

            lastUpdateTime = DateTime.Now;

            liveSpectrum = newSpectrum;

            // If caller supplied an X-axis (HI modes), use it
            if (newXAxis != null)
                xAxis = newXAxis;
            else
                xAxis = frequencies; // IF mode

            App.Current.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < FftSize; i++)
                {
                    points[i].X = xAxis[i];
                    points[i].Y = liveSpectrum[i];
                }
            });

            // Apply axis mode (labels, limits, units)
            ApplyAxisMode();

            // Auto-scale Y-axis
            YAxes[0].MinLimit = liveSpectrum.Min() - 5;
            YAxes[0].MaxLimit = liveSpectrum.Max() + 5;
        }

        public void UpdateSpectrum(double[] newSpectrum)
        {
            if (DateTime.Now - lastUpdateTime < TimeSpan.FromMilliseconds(50))
            {
                // Skip this update to throttle the refresh rate
                return;
            }
            lastUpdateTime = DateTime.Now;

            liveSpectrum = newSpectrum;
            App.Current.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < FftSize; i++)
                {
                    points[i].Y = liveSpectrum[i];
                }
            });

            YAxes[0].MinLimit = liveSpectrum.Min() - 5; // Add some padding
            YAxes[0].MaxLimit = liveSpectrum.Max() + 5; // Add some padding

        }

        

        // ---------------------------------------------------------
        // INTERNAL REBUILD LOGIC
        // ---------------------------------------------------------
        private void BuildFrequencyAxis()
        {

            double binWidth = SamplingFrequencyHz / FftSize;
            double startFreq = CenterFreqHz - (SamplingFrequencyHz / 2);

            frequencies = Enumerable.Range(0, FftSize)
                .Select(i => startFreq + i * binWidth)
                .ToArray();

            liveSpectrum = new double[FftSize]; // Initialize with zeros
            points = new ObservablePoint[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                liveSpectrum[i] = 0.0;
                points[i] = new ObservablePoint(frequencies[i], liveSpectrum[i]);
            }
            Series[0].Values = points;
        }
    }
}
