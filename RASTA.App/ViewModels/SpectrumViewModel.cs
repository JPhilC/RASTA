using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace RASTA.App.ViewModels
{
    public partial class SpectrumViewModel : ObservableObject
    {
        // FFT bin values (Y-axis)
        [ObservableProperty]
        private double[]? liveSpectrum;

        // Frequency axis (X-axis)
        [ObservableProperty]
        private double[]? frequencies;

        public ISeries[] Series { get; private set; }
        public Axis[] XAxes { get; private set; }
        public Axis[] YAxes { get; private set; }

        // Backing fields
        private int _fftSize;
        public int FftSize
        {
            get => _fftSize;
            set
            {
                if (_fftSize != value)
                {
                    _fftSize = value;
                    OnPropertyChanged(nameof(FftSize));
                    UpdateParameters();
                }
            }
        }

        private double _centerFreqHz;
        public double CenterFreqHz
        {
            get => _centerFreqHz;
            set
            {
                if (_centerFreqHz != value)
                {
                    _centerFreqHz = value;
                    OnPropertyChanged(nameof(CenterFreqHz));
                    UpdateParameters();
                }
            }
        }

        private double _samplingFrequencyHz;
        public double SamplingFrequencyHz
        {
            get => _samplingFrequencyHz;
            set
            {
                if (_samplingFrequencyHz != value)
                {
                    _samplingFrequencyHz = value;
                    OnPropertyChanged(nameof(SamplingFrequencyHz));
                    UpdateParameters();
                }
            }
        }

        public SpectrumViewModel(int fftSize, double centerFreqHz, double sampleRateHz)
        {
            _fftSize = fftSize;
            _centerFreqHz = centerFreqHz;
            _samplingFrequencyHz = sampleRateHz;

            BuildAll();
        }

        // ---------------------------------------------------------
        // PUBLIC API — called by ObserveViewModel
        // ---------------------------------------------------------
        public void UpdateSpectrum(double[] newSpectrum)
        {
            System.Diagnostics.Debug.WriteLine($"Update spectrum called with {newSpectrum.Min()} - {newSpectrum.Max()}");
            LiveSpectrum = newSpectrum.ToArray();

            Series = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = LiveSpectrum,
                    Fill = null,
                    GeometrySize = 0,
                    Stroke = new SolidColorPaint(new SKColor(0, 200, 255)),
                    LineSmoothness = 0
                }
            };

            OnPropertyChanged(nameof(Series));
        }

        // ---------------------------------------------------------
        // INTERNAL REBUILD LOGIC
        // ---------------------------------------------------------
        private void BuildAll()
        {
            RebuildSpectrumArrays();
            RebuildSeries();
            RebuildAxes();
        }

        private void UpdateParameters()
        {
            BuildAll();
        }

        private void RebuildSpectrumArrays()
        {
            LiveSpectrum = new double[FftSize];

            double binWidth = SamplingFrequencyHz / FftSize;
            double startFreq = CenterFreqHz - (SamplingFrequencyHz / 2);

            Frequencies = Enumerable.Range(0, FftSize)
                .Select(i => startFreq + i * binWidth)
                .ToArray();
        }

        private void RebuildSeries()
        {
            Series = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = LiveSpectrum,
                    Fill = null,
                    GeometrySize = 0,
                    Stroke = new SolidColorPaint(new SKColor(0, 200, 255)),
                    LineSmoothness = 0
                }
            };
        }

        private void RebuildAxes()
        {
            XAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Frequency (Hz)",
                    LabelsRotation = 45,
                    MinLimit = Frequencies.First(),
                    MaxLimit = Frequencies.Last()
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
    }
}
