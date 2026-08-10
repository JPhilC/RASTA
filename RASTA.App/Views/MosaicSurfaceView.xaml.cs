using HelixToolkit.Wpf;
using RASTA.App.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    /// Colour comes from a LinearGradientBrush (see BuildGradientBrush) sampled per-vertex via
    /// standard mesh texture coordinates (U only), since classic WPF 3D has no native
    /// per-vertex colour - the same diverging blue-gray-red ramp MosaicViewModel's 2D
    /// heatmaps use (HeatmapImageBuilder.DivergingStops), so the flat and 3D views colour
    /// identically. A raster ImageBrush wrapping a BitmapSource was tried first and never
    /// rendered here (see BuildGradientBrush's remarks) - a vector gradient brush avoids
    /// whatever WPF's hardware 3D texture pipeline was rejecting about that raster texture.
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

        public static readonly DependencyProperty SmoothProperty =
            DependencyProperty.Register(nameof(Smooth), typeof(bool), typeof(MosaicSurfaceView),
                new PropertyMetadata(false, OnDataChanged));

        /// <summary>
        /// Bound to MosaicViewModel.UseSmoothBlend - see UpsampleBilinear's remarks. Off by
        /// default, matching the 2D heatmap's own "Smooth blend" default.
        /// </summary>
        public bool Smooth
        {
            get => (bool)GetValue(SmoothProperty);
            set => SetValue(SmoothProperty, value);
        }

        public static readonly DependencyProperty XTicksProperty =
            DependencyProperty.Register(nameof(XTicks), typeof(IReadOnlyList<AxisTick>), typeof(MosaicSurfaceView),
                new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty YTicksProperty =
            DependencyProperty.Register(nameof(YTicks), typeof(IReadOnlyList<AxisTick>), typeof(MosaicSurfaceView),
                new PropertyMetadata(null, OnDataChanged));

        /// <summary>Real RA/Az axis values (not normalized model-space) - see BuildAxes.</summary>
        public IReadOnlyList<AxisTick>? XTicks
        {
            get => (IReadOnlyList<AxisTick>?)GetValue(XTicksProperty);
            set => SetValue(XTicksProperty, value);
        }

        /// <summary>Real Dec/El axis values (not normalized model-space) - see BuildAxes.</summary>
        public IReadOnlyList<AxisTick>? YTicks
        {
            get => (IReadOnlyList<AxisTick>?)GetValue(YTicksProperty);
            set => SetValue(YTicksProperty, value);
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
            AxisVisual.Children.Clear();
            AxisVisual.Content = null;

            var grid = IntensityGrid;
            var xValues = XValues;
            var yValues = YValues;
            if (grid == null || xValues == null || yValues == null)
                return;

            int width = grid.GetLength(0);
            int height = grid.GetLength(1);
            if (width < 2 || height < 2 || width != xValues.Length || height != yValues.Length)
                return;

            if (Smooth)
            {
                (grid, xValues, yValues) = UpsampleBilinear(grid, xValues, yValues, SmoothSubdivisionFactor);
                width = grid.GetLength(0);
                height = grid.GetLength(1);
            }

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
            var vertexIndex = new int[width, height];

            for (int gx = 0; gx < width; gx++)
            {
                for (int gy = 0; gy < height; gy++)
                {
                    double v = grid[gx, gy];
                    bool valid = !double.IsNaN(v);

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
                    // Every corner already has a position (real height, or the zero-height
                    // fallback for a NaN/no-data cell - see above), so every quad gets drawn.
                    // This used to skip any quad touching a no-data corner, which sounds
                    // reasonable but meant a genuinely sparse mosaic - a handful of scattered
                    // positions on the full-sky grid, or a single-row/single-column sweep where
                    // no two adjacent grid cells in BOTH axes ever both have data - produced
                    // zero triangles, i.e. a mesh with real Positions but nothing visible: the
                    // viewport then shows only its fixed corner overlays (coordinate triad,
                    // view cube) with an empty main scene, which also makes mouse-wheel zoom
                    // look broken since there's nothing to zoom into.
                    int i00 = vertexIndex[gx, gy];
                    int i10 = vertexIndex[gx + 1, gy];
                    int i01 = vertexIndex[gx, gy + 1];
                    int i11 = vertexIndex[gx + 1, gy + 1];

                    indices.Add(i00); indices.Add(i11); indices.Add(i10);
                    indices.Add(i00); indices.Add(i01); indices.Add(i11);
                }
            }

            // Explicit per-vertex normals rather than relying on WPF's automatic normal
            // generation: HelixToolkit's own MeshBuilder-based visuals (see the GridLinesVisual3D/
            // SphereVisual3D diagnostic added while chasing RASTA issue #13's "nothing renders"
            // report) always compute Normals explicitly, and auto-generation for a bare
            // MeshGeometry3D with only Positions/TriangleIndices set is known to be unreliable
            // under WPF's software rendering tier (RDP sessions, VMs, some driver setups) -
            // exactly the symptom seen: geometry present (it affects ZoomExtents' bounds) but
            // nothing visibly drawn. Smooth (averaged) per-vertex shading, matching a height
            // field's continuous surface.
            var normalSums = new Vector3D[positions.Count];
            for (int t = 0; t < indices.Count; t += 3)
            {
                int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                var p0 = positions[i0];
                var edge1 = positions[i1] - p0;
                var edge2 = positions[i2] - p0;
                var faceNormal = Vector3D.CrossProduct(edge1, edge2);
                normalSums[i0] += faceNormal;
                normalSums[i1] += faceNormal;
                normalSums[i2] += faceNormal;
            }

            var normals = new Vector3DCollection(positions.Count);
            foreach (var n in normalSums)
            {
                var normal = n;
                if (normal.LengthSquared > 1e-12)
                    normal.Normalize();
                else
                    normal = new Vector3D(0, 1, 0); // vertex touched by no valid triangle - shouldn't happen given the width/height>=2 guard above, but keep a sane fallback
                normals.Add(normal);
            }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = indices,
                TextureCoordinates = texCoords,
                Normals = normals
            };

            // Translucent, not opaque: the reference grid/labels sit at Y=0 (see FloorY), which
            // for data whose range straddles zero (e.g. Velocity mode, symmetric about 0 km/s)
            // sits partway *through* the mesh, not below it - an opaque surface would hide
            // whatever part of the grid/labels falls behind it from the current view angle.
            var material = new DiffuseMaterial(TranslucentGradientBrush);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };

            SurfaceVisual.Content = model;
            BuildAxes(NormX, NormZ);
            Viewport.ZoomExtents(0);
        }

        // Floor level for the reference grid/labels - the plot's own Y=0 (the height field's
        // zero reference, not the data's min/max), rather than pinned to the bottom of whatever
        // range the current session happens to span. For Velocity mode Y=0 is a real, physically
        // meaningful plane (0 km/s, the LSR-corrected line center); for Strength/dB it's just a
        // stable, data-independent mid-height reference. The mesh material is translucent (see
        // TranslucentGradientBrush) precisely because this plane routinely sits *through* rather
        // than below the surface, which would otherwise hide the grid/labels behind it.
        private const double FloorY = 0.0;

        // How far outside the PlaneExtent x PlaneExtent floor the tick labels sit, along
        // whichever axis they're not measuring - just enough clearance to read separately from
        // the gridlines themselves without drifting far from the surface they're labelling.
        private const double LabelOffset = 0.6;
        private const double LabelHeight = 0.35;

        // Both X and Y tick labels use the SAME textDirection/updirection - matching
        // HelixToolkit's own SurfacePlotVisual3D reference example (which labels every axis
        // with one consistent direction pair rather than rotating each axis's labels to "face"
        // its own direction). Using a different updirection per axis is what originally produced
        // upside-down X labels: flat, non-billboarded text has no way to "always face the
        // camera", so the only robust choice is one fixed, predictable orientation for every
        // label - readable from a top-down view (HelixViewport3D's ViewCube "Top" corner).
        private static readonly Vector3D LabelTextDirection = new(1, 0, 0);
        private static readonly Vector3D LabelUpDirection = new(0, 0, 1);

        /// <summary>
        /// Builds a faint reference floor grid plus tick labels at XTicks/YTicks' real RA/Dec(/
        /// Az/El) values, mapped into the same normalized model space the mesh uses via the
        /// caller's NormX/NormZ closures - so a gridline/label lines up exactly with the mesh
        /// data it's next to, however the session's real coordinate range happens to sit within
        /// the fixed PlaneExtent display box.
        ///
        /// Gridlines use LinesVisual3D (a screen-space-constant-width line strip with a plain
        /// SolidColorBrush-backed material - the same "vector, not raster" family as
        /// BuildGradientBrush's LinearGradientBrush) rather than HelixToolkit's GridLinesVisual3D,
        /// since GridLinesVisual3D's own Center/MinorDistance parameterization doesn't have an
        /// easy way to force gridlines to land exactly on our already-computed nice-tick
        /// positions - building the segments by hand guarantees a gridline and its label always
        /// refer to the identical position.
        ///
        /// Tick text uses HelixToolkit's TextCreator.CreateTextLabelModel3D, which (unlike
        /// TextVisual3D/BillboardTextVisual3D - see MosaicSurfaceView's own history with this
        /// exact machine's WPF 3D render tier) renders its DiffuseMaterial via a VisualBrush
        /// wrapping a live TextBlock rather than a pre-rasterized BitmapSource, putting it in the
        /// same "vector brush" family that's actually proven to render here. The label lies flat
        /// on the floor plane (not billboarded to face the camera), so it reads best from close
        /// to a top-down view - HelixViewport3D's ViewCube "Top" corner gets there in one click.
        /// </summary>
        private void BuildAxes(Func<double, double> normX, Func<double, double> normZ)
        {
            var xTicks = XTicks;
            var yTicks = YTicks;
            if (xTicks is null || yTicks is null)
                return;

            const double half = PlaneExtent / 2;

            var linePoints = new Point3DCollection();
            var labels = new Model3DGroup();

            foreach (var tick in xTicks)
            {
                double x = normX(tick.Position);
                linePoints.Add(new Point3D(x, FloorY, -half));
                linePoints.Add(new Point3D(x, FloorY, half));

                labels.Children.Add(TextCreator.CreateTextLabelModel3D(
                    tick.Label, Brushes.DimGray, isDoubleSided: true, height: LabelHeight,
                    center: new Point3D(x, FloorY, -half - LabelOffset),
                    textDirection: LabelTextDirection, updirection: LabelUpDirection));
            }

            foreach (var tick in yTicks)
            {
                double z = normZ(tick.Position);
                linePoints.Add(new Point3D(-half, FloorY, z));
                linePoints.Add(new Point3D(half, FloorY, z));

                labels.Children.Add(TextCreator.CreateTextLabelModel3D(
                    tick.Label, Brushes.DimGray, isDoubleSided: true, height: LabelHeight,
                    center: new Point3D(-half - LabelOffset, FloorY, z),
                    textDirection: LabelTextDirection, updirection: LabelUpDirection));
            }

            AxisVisual.Children.Add(new LinesVisual3D { Points = linePoints, Color = Colors.Gray, Thickness = 0.6 });
            AxisVisual.Content = labels;
        }

        // How many extra rows/columns get interpolated between each pair of real cell centers
        // when Smooth is on - 4 turns e.g. a 5x5 measured grid into a 17x17 mesh, enough to read
        // as a continuous, rounded surface rather than faceted quads without ballooning the
        // triangle count for a large full-sky-grid session.
        private const int SmoothSubdivisionFactor = 4;

        /// <summary>
        /// Subdivides a coarse grid into a `factor`x finer one via bilinear interpolation
        /// between neighbouring cell centers - the same technique HeatmapImageBuilder.
        /// BuildBlended uses for the 2D heatmap's "Smooth blend" mode, applied here to the
        /// mesh's actual geometry instead of just its rendered colour. A raw one-quad-per-cell
        /// mesh reads as sharp, low-poly facets - especially "pointy" spikes where a single
        /// measured cell sits surrounded by the zero-height NaN fallback (see Rebuild's
        /// quad-generation remarks) - interpolating extra rows/columns between real cell centers
        /// turns that into a continuous, rounded-off surface without reprocessing the session or
        /// inventing new measurements far from real ones: NaN corners are dropped from the
        /// weighted average (renormalized over whichever of the 4 are present, exactly matching
        /// BuildBlended), so a genuine gap of 2+ unmeasured cells between real data still tapers
        /// toward the zero-height fallback rather than being smoothed over, and a destination
        /// cell with no real corners at all stays NaN. Real cell centers land exactly on the
        /// output grid (every `factor`-th point), so smoothing never moves an actual measurement.
        /// </summary>
        private static (double[,] grid, double[] xValues, double[] yValues) UpsampleBilinear(
            double[,] grid, double[] xValues, double[] yValues, int factor)
        {
            int srcWidth = grid.GetLength(0);
            int srcHeight = grid.GetLength(1);
            int dstWidth = (srcWidth - 1) * factor + 1;
            int dstHeight = (srcHeight - 1) * factor + 1;

            (int i0, double frac) Locate(double t, int srcCount)
            {
                int i0 = Math.Min((int)t, srcCount - 2);
                return (i0, t - i0);
            }

            var dstX = new double[dstWidth];
            for (int i = 0; i < dstWidth; i++)
            {
                var (gx0, fx) = Locate(i / (double)factor, srcWidth);
                dstX[i] = xValues[gx0] + fx * (xValues[gx0 + 1] - xValues[gx0]);
            }

            var dstY = new double[dstHeight];
            for (int j = 0; j < dstHeight; j++)
            {
                var (gy0, fy) = Locate(j / (double)factor, srcHeight);
                dstY[j] = yValues[gy0] + fy * (yValues[gy0 + 1] - yValues[gy0]);
            }

            var dst = new double[dstWidth, dstHeight];
            for (int i = 0; i < dstWidth; i++)
            {
                var (gx0, fx) = Locate(i / (double)factor, srcWidth);
                for (int j = 0; j < dstHeight; j++)
                {
                    var (gy0, fy) = Locate(j / (double)factor, srcHeight);
                    dst[i, j] = BilinearSample(grid, gx0, gy0, fx, fy);
                }
            }

            return (dst, dstX, dstY);
        }

        private static double BilinearSample(double[,] grid, int gx0, int gy0, double fx, double fy)
        {
            double? Sample(int gx, int gy)
            {
                double v = grid[gx, gy];
                return double.IsNaN(v) ? (double?)null : v;
            }

            double sum = 0, weight = 0;
            void Accum(double? v, double w)
            {
                if (v.HasValue)
                {
                    sum += v.Value * w;
                    weight += w;
                }
            }

            Accum(Sample(gx0, gy0), (1 - fx) * (1 - fy));
            Accum(Sample(gx0 + 1, gy0), fx * (1 - fy));
            Accum(Sample(gx0, gy0 + 1), (1 - fx) * fy);
            Accum(Sample(gx0 + 1, gy0 + 1), fx * fy);

            return weight > 0 ? sum / weight : double.NaN;
        }

        /// <summary>
        /// Builds the same diverging blue-gray-red ramp as HeatmapImageBuilder, as a
        /// LinearGradientBrush rather than an ImageBrush wrapping a rasterized BitmapSource.
        /// A raster ImageBrush (tried first, at both 1px and 2px tall) never rendered as a 3D
        /// material here despite the exact same mesh/normals rendering fine with a plain
        /// SolidColorBrush - consistent with WPF's hardware 3D texture pipeline failing to
        /// bind/mipmap an extreme-aspect-ratio raster texture. A vector LinearGradientBrush
        /// sidesteps that: WPF rasterizes it to a texture internally using its own sizing, and
        /// mapping a single axis (U only - see the Point(u, 0)/(0.5, 0) texture coordinates
        /// above) onto GradientStops is exactly what this brush type is for.
        /// </summary>
        /// <param name="alpha">
        /// Baked into every stop's Color rather than set via Brush.Opacity, since the brush is
        /// Frozen (immutable) once built - see TranslucentGradientBrush's remarks for why the
        /// surface itself needs to be translucent, not opaque.
        /// </param>
        private static LinearGradientBrush BuildGradientBrush(byte alpha)
        {
            var stops = new GradientStopCollection();
            var colors = HeatmapImageBuilder.DivergingStops;
            for (int i = 0; i < colors.Length; i++)
            {
                var (r, g, b) = colors[i];
                double offset = i / (double)(colors.Length - 1);
                stops.Add(new GradientStop(Color.FromArgb(alpha, r, g, b), offset));
            }

            var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
            brush.Freeze();
            return brush;
        }

        // ~78% opaque - visible enough to read the surface's own colour clearly, translucent
        // enough that the Y=0 reference grid/labels (see FloorY) remain legible through it from
        // whichever side of Y=0 the current view angle puts them on.
        private static readonly LinearGradientBrush TranslucentGradientBrush = BuildGradientBrush(200);
    }
}
