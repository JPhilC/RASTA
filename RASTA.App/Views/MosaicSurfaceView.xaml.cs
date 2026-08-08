using RASTA.App.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace RASTA.App.Views
{
    /// <summary>
    /// Renders MosaicViewModel's sky grid (SurfaceIntensityGrid/SurfaceXValues/SurfaceYValues -
    /// RA x Dec x LineStrengthDb) as a rotatable 3D height-field surface via HelixToolkit.Wpf's
    /// HelixViewport3D. The mesh itself is built by hand from plain WPF 3D types
    /// (Point3DCollection/Int32Collection) rather than HelixToolkit.Geometry.MeshBuilder,
    /// which in this Helix version works in System.Numerics.Vector3 and would need its own
    /// conversion step anyway - HelixToolkit's real value here is the interactive
    /// camera/viewport (rotate/zoom/pan), not mesh construction.
    ///
    /// Colour comes from a 1D gradient texture (see HeatmapImageBuilder.Ramp) sampled per-
    /// vertex via standard mesh texture coordinates, since classic WPF 3D has no native
    /// per-vertex colour - the same diverging blue-gray-red ramp MosaicViewModel's 2D
    /// heatmaps use (HeatmapImageBuilder), so the flat and 3D views colour identically.
    ///
    /// Rebuilds both when its bound data changes AND when it becomes visible: a HelixViewport3D
    /// sitting inside a TabItem that was never selected has no layout to frame a camera against,
    /// so a rebuild that lands while the "3D Surface" tab is hidden needs to redo the zoom-to-fit
    /// once the tab is actually shown, or the view stays blank until something else nudges it.
    /// </summary>
    public partial class MosaicSurfaceView : UserControl
    {
        public static readonly DependencyProperty IntensityGridProperty =
            DependencyProperty.Register(nameof(IntensityGrid), typeof(double[,]), typeof(MosaicSurfaceView),
                new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty XValuesProperty =
            DependencyProperty.Register(nameof(XValues), typeof(double[]), typeof(MosaicSurfaceView),
                new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty YValuesProperty =
            DependencyProperty.Register(nameof(YValues), typeof(double[]), typeof(MosaicSurfaceView),
                new PropertyMetadata(null, OnDataChanged));

        public double[,]? IntensityGrid
        {
            get => (double[,]?)GetValue(IntensityGridProperty);
            set => SetValue(IntensityGridProperty, value);
        }

        public double[]? XValues
        {
            get => (double[]?)GetValue(XValuesProperty);
            set => SetValue(XValuesProperty, value);
        }

        public double[]? YValues
        {
            get => (double[]?)GetValue(YValuesProperty);
            set => SetValue(YValuesProperty, value);
        }

        public MosaicSurfaceView()
        {
            InitializeComponent();
            Loaded += (_, __) => Rebuild();
            // Re-fit the camera once the "3D Surface" tab is actually shown, deferred a
            // layout pass past the visibility flip so the viewport has real bounds to frame
            // against (ZoomExtents against a not-yet-measured, never-attached viewport would
            // otherwise compute a degenerate camera and leave the surface looking blank).
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                    Dispatcher.InvokeAsync(Rebuild, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((MosaicSurfaceView)d).Rebuild();

        // Visual footprint of the surface, independent of the data's real units (velocity in
        // km/s, position as an integer index) - every axis is normalized into this box so the
        // camera's default framing looks sensible regardless of the session's actual scale.
        private const double PlaneExtent = 10.0;
        private const double HeightExtent = 3.0;

        private void Rebuild()
        {
            SurfaceVisual.Content = null;

            var grid = IntensityGrid;
            var xValues = XValues;
            var yValues = YValues;
            if (grid == null || xValues == null || yValues == null)
                return;

            int width = grid.GetLength(0);
            int height = grid.GetLength(1);
            if (width < 2 || height < 2 || width != xValues.Length || height != yValues.Length)
                return;

            double xMin = xValues.Min(), xMax = xValues.Max();
            double yMin = yValues.Min(), yMax = yValues.Max();

            double zMin = double.MaxValue, zMax = double.MinValue;
            for (int gx = 0; gx < width; gx++)
            {
                for (int gy = 0; gy < height; gy++)
                {
                    double v = grid[gx, gy];
                    if (double.IsNaN(v)) continue;
                    if (v < zMin) zMin = v;
                    if (v > zMax) zMax = v;
                }
            }
            if (zMin > zMax)
                return; // every cell is NaN - nothing to draw

            double xRange = Math.Max(xMax - xMin, 1e-9);
            double yRange = Math.Max(yMax - yMin, 1e-9);
            double zRange = Math.Max(zMax - zMin, 1e-9);

            double NormX(double x) => (x - xMin) / xRange * PlaneExtent - PlaneExtent / 2;
            double NormZ(double y) => (y - yMin) / yRange * PlaneExtent - PlaneExtent / 2;
            double NormHeight(double v) => (v - zMin) / zRange * HeightExtent - HeightExtent / 2;
            double NormColor(double v) => (v - zMin) / zRange;

            var positions = new Point3DCollection(width * height);
            var texCoords = new PointCollection(width * height);
            var hasData = new bool[width, height];
            var vertexIndex = new int[width, height];

            for (int gx = 0; gx < width; gx++)
            {
                for (int gy = 0; gy < height; gy++)
                {
                    double v = grid[gx, gy];
                    bool valid = !double.IsNaN(v);
                    hasData[gx, gy] = valid;

                    positions.Add(new Point3D(NormX(xValues[gx]), valid ? NormHeight(v) : 0, NormZ(yValues[gy])));
                    texCoords.Add(new Point(valid ? NormColor(v) : 0.5, 0));
                    vertexIndex[gx, gy] = gx * height + gy;
                }
            }

            var indices = new Int32Collection();
            for (int gx = 0; gx < width - 1; gx++)
            {
                for (int gy = 0; gy < height - 1; gy++)
                {
                    // Skip any cell touching a no-data (NaN) corner rather than drawing a
                    // triangle down to the fallback zero-height used above for its position.
                    if (!hasData[gx, gy] || !hasData[gx + 1, gy] || !hasData[gx, gy + 1] || !hasData[gx + 1, gy + 1])
                        continue;

                    int i00 = vertexIndex[gx, gy];
                    int i10 = vertexIndex[gx + 1, gy];
                    int i01 = vertexIndex[gx, gy + 1];
                    int i11 = vertexIndex[gx + 1, gy + 1];

                    indices.Add(i00); indices.Add(i11); indices.Add(i10);
                    indices.Add(i00); indices.Add(i01); indices.Add(i11);
                }
            }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = indices,
                TextureCoordinates = texCoords
            };

            var material = new DiffuseMaterial(new ImageBrush(GradientBitmap));
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };

            SurfaceVisual.Content = model;
            Viewport.ZoomExtents(0);
        }

        private static readonly BitmapSource GradientBitmap = HeatmapImageBuilder.BuildLegendStrip(256, 1);
    }
}
