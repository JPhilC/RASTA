using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using OpenTK.Graphics.OpenGL;
using RASTA.App.Helpers;
using RASTA.Processing.HiPipeline;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Configuration;

namespace RASTA.App.ViewModels
{
    public enum SpectrumMode
    {
        HiFrequency,
        HiVelocity,
        TTRT,
        Ratio
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

        // A single zero-width RectangularSection (Xi == Xj) rendered as a vertical dashed
        // line marking the unshifted HI rest position - 1420.40575177 MHz for the frequency-
        // axis modes (HiFrequency/TTRT/Ratio; that axis is never LSR-corrected) or 0 km/s for
        // HiVelocity (which IS LSR-corrected, so 0 km/s means "at rest relative to the LSR" -
        // real HI emission still shows up offset from it due to the source's own galactic
        // kinematics, so the line stays a meaningful reference rather than always matching
        // the peak). See VisualiseViewModel.ProcessHiCore / AstronomyUtils.ComputeLsrCorrectionKmPerSec.
        public RectangularSection[] Sections { get; }

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

            Sections = new RectangularSection[]
            {
                new RectangularSection
                {
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.Red)
                    {
                        StrokeThickness = 1.5f,
                        PathEffect = new DashEffect(new float[] { 6, 4 })
                    }
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

            UpdateHiReferenceLine();
        }

        partial void OnModeChanged(SpectrumMode value) => UpdateHiReferenceLine();

        /// <summary>
        /// Positions the vertical dashed reference line (<see cref="Sections"/>) at the
        /// unshifted HI rest position for the current <see cref="Mode"/>: 0 km/s for
        /// HiVelocity (that axis is LSR-corrected, so 0 is "at rest relative to the LSR"),
        /// or the static HI rest frequency for the frequency-axis modes (HiFrequency/TTRT/
        /// Ratio), which are never LSR-corrected.
        /// </summary>
        private void UpdateHiReferenceLine()
        {
            double referenceX = Mode == SpectrumMode.HiVelocity ? 0.0 : HiConstants.HiFreqHz;
            Sections[0].Xi = referenceX;
            Sections[0].Xj = referenceX;
        }

        private void ApplyAxisMode()
        {
            switch (Mode)
            {
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

                case SpectrumMode.Ratio:
                    // Bandpass-flattened capture/baseline ratio, before continuum
                    // subtraction - strictly positive, so this is where a dB toggle
                    // (set by the caller after UpdateSpectrum) is meaningful.
                    XAxes[0].Name = "Frequency (MHz)";
                    XAxes[0].Labeler = value => $"{value / 1_000_000.0:F2} MHz";
                    XAxes[0].MinLimit = xAxis.First();
                    XAxes[0].MaxLimit = xAxis.Last();
                    XAxes[0].MinStep = ComputeBinStep(xAxis);
                    YAxes[0].Name = "Ratio";
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
        // PUBLIC API — called by CaptureViewModel
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

            // If caller supplied an X-axis (HI modes), use it; otherwise fall back to the
            // plain frequency axis (standalone baseline/capture charts).
            if (newXAxis != null)
                xAxis = newXAxis;
            else
                xAxis = frequencies;

            // Called from CaptureViewModel.ChunkWorker's background Task.Run loop during
            // any live sweep/Quick Capture, so everything touching the UI-bound chart
            // (points, plus ApplyAxisMode/ApplyRobustYAxisRange below, which mutate the
            // LiveChartsCore Axis objects bound to SpectrumView) must be marshaled onto the
            // UI thread. UiThread.SafeInvoke (rather than a raw App.Current.Dispatcher.
            // Invoke) also tolerates the app-shutdown window where Application.Current can
            // go null before ChunkWorker has fully stopped - see CaptureViewModel.
            // LoadAvailablePlans/RASTA.App.Helpers.UiThread.
            UiThread.SafeInvoke(() =>
            {
                for (int i = 0; i < FftSize; i++)
                {
                    points[i].X = xAxis[i];
                    points[i].Y = liveSpectrum[i];
                }

                // Apply axis mode (labels, limits, units)
                ApplyAxisMode();

                // Auto-scale Y-axis
                ApplyRobustYAxisRange(liveSpectrum);
            });
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

            // See the other UpdateSpectrum overload above for why this needs UiThread.
            // SafeInvoke rather than a raw Dispatcher.Invoke.
            UiThread.SafeInvoke(() =>
            {
                for (int i = 0; i < FftSize; i++)
                {
                    points[i].Y = liveSpectrum[i];
                }

                ApplyRobustYAxisRange(liveSpectrum);
            });
        }

        /// <summary>
        /// Sets the Y-axis range from the 1st/99th percentile of the data plus a margin,
        /// rather than raw Min()/Max(). A handful of receiver-artifact/RFI bins (e.g. the
        /// fixed-offset SDR spur seen at ~+100kHz from center, which - unlike the DC/LO
        /// leakage at the tuned center - isn't visible in the baseline and so can't be
        /// safely excised from the data itself, see HiStreamingPipeline.RemoveDcSpike) can
        /// otherwise dominate a raw-min/max autoscale and squash the genuine spectral shape
        /// into a flat line. This never touches the underlying data - only how much of the
        /// Y range the chart devotes to outliers versus the real signal.
        /// </summary>
        private void ApplyRobustYAxisRange(double[] data)
        {
            if (data.Length == 0)
                return;

            var sorted = (double[])data.Clone();
            Array.Sort(sorted);
            int n = sorted.Length;

            double Percentile(double p)
            {
                double idx = p * (n - 1);
                int lo = (int)Math.Floor(idx);
                int hi = (int)Math.Ceiling(idx);
                if (lo == hi) return sorted[lo];
                double frac = idx - lo;
                return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
            }

            double p1 = Percentile(0.01);
            double p99 = Percentile(0.99);
            double range = p99 - p1;
            double margin = range > 0 ? range * 0.15 : 5; // flat data (e.g. the zeroed reset spectrum) falls back to +-5

            YAxes[0].MinLimit = p1 - margin;
            YAxes[0].MaxLimit = p99 + margin;
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
