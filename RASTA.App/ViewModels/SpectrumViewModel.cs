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
    public partial class SpectrumViewModel : ObservableObject
    {
        private ObservablePoint[] points;
        
            // FFT bin values (Y-axis)
        private double[]? liveSpectrum;

        // Frequency axis (X-axis)
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
            XAxes[0].MinStep = 100_000; // 100 kHz step

            YAxes[0].MinLimit = -50d;
            YAxes[0].MaxLimit = 50d;

        }


        private DateTime lastUpdateTime = DateTime.MinValue;

        
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

        //private void BuildFrequencyAxis()
        //{
        //     Initialise spectrum buffer
        //    liveSpectrum = new double[FftSize];

        //    Series[0].Values = liveSpectrum;

        //    double binWidthHz = SamplingFrequencyHz / FftSize;
        //    double startFreqHz = CenterFreqHz - (SamplingFrequencyHz / 2);

        //     Convert to MHz with 2 decimals
        //    frequencies = Enumerable.Range(0, FftSize)
        //        .Select(i =>
        //        {
        //            double freqHz = startFreqHz + i * binWidthHz;
        //            return Math.Round(freqHz / 1_000_000.0, 2);   // MHz
        //        })
        //        .ToArray();
        //}
    }
}
