using HelixToolkit.Wpf;
using RASTA.App.Helpers;
using RASTA.App.ViewModels;
using RASTA.Processing.Gridding;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace RASTA.App.Views
{
    /// <summary>
    /// Renders MosaicViewModel.SurfacePoints (every currently-visible-above-the-horizon position's
    /// live Az/El plus whichever metric SurfaceMetric selects - see MosaicViewModel.RenderSurface)
    /// as a 3D extrusion of the same dome the 2D Zenith Dome tab draws flat: reference altitude
    /// rings/azimuth spokes/compass labels sit on the Z=0 ground plane exactly like the 2D dome's
    /// pixel-space rings/spokes/labels, and each point becomes a thin cylinder "stem" standing up
    /// (or down, for a negative value) from that plane to a sphere "tip" - height proportional to
    /// the point's own value, not a bump/dent from a fixed base shell the way the old spherical-
    /// globe 3D Surface worked. This tab used to render MosaicViewModel's persistent RA/Dec/Az-El
    /// GridBuilder height-field globe (see git history) - replaced entirely because the sinusoidal-
    /// projection-carrying globe read as hard to interpret; a flat, familiar dome-with-stems layout
    /// shared with the already-legible 2D Zenith Dome is a more direct "show me what's up and how
    /// strong/fast it is" view.
    ///
    /// Reuses every hard-won HelixToolkit lesson the old globe surfaced (see its own history):
    /// explicit per-vertex Normals (WPF's automatic normal generation is unreliable on this
    /// machine's WPF 3D render tier for a hand-built MeshGeometry3D), a vector LinearGradientBrush
    /// material rather than a raster ImageBrush (which silently failed to render as a 3D material
    /// here), HelixToolkit.Wpf.TextCreator for labels (renders via a VisualBrush, not a rasterized
    /// ImageBrush, so it's in the same "proven to render" family), and a translucent material
    /// (BackMaterial set to the same brush) since the Z=0 reference grid can sit through rather
    /// than under the data for a metric whose range straddles zero (Velocity, or Strength dipping
    /// slightly negative).
    ///
    /// Unlike the globe, every shape here (cylinder, sphere) is a small, self-contained,
    /// parametric solid always viewed from outside (the camera is never inside a stem or dot the
    /// way the globe was dead-center inside its own shell), so normals are computed directly from
    /// each shape's own geometry (radial-from-axis for a cylinder, radial-from-center for a
    /// sphere) rather than by averaging face normals afterward - simpler and exact for a known
    /// parametric shape. The optional fitted mesh (see BuildFittedMesh) is the one part that IS a
    /// general, irregular surface, so it reuses the globe's own face-normal-averaging technique.
    /// </summary>
    public partial class MosaicDomeSurfaceView : UserControl
    {
        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<DomeSurfacePoint>), typeof(MosaicDomeSurfaceView),
                new PropertyMetadata(null, OnDataChanged));

        public IReadOnlyList<DomeSurfacePoint>? Points
        {
            get => (IReadOnlyList<DomeSurfacePoint>?)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public static readonly DependencyProperty FitMeshProperty =
            DependencyProperty.Register(nameof(FitMesh), typeof(bool), typeof(MosaicDomeSurfaceView),
                new PropertyMetadata(false, OnDataChanged));

        /// <summary>
        /// When true, replaces the per-point stems/dots with a Delaunay-triangulated translucent
        /// surface through each point's own extruded (x, height, z) position (see BuildFittedMesh)
        /// - the two are alternate readings of the same data (the mesh already passes through
        /// every stem's own tip), so showing both at once would mostly just double up on the same
        /// shape. Bound from MosaicViewModel.FitMeshThroughPoints.
        /// </summary>
        public bool FitMesh
        {
            get => (bool)GetValue(FitMeshProperty);
            set => SetValue(FitMeshProperty, value);
        }

        public MosaicDomeSurfaceView()
        {
            InitializeComponent();
            Loaded += (_, __) => Rebuild();
            // Same reasoning as the old globe view: a HelixViewport3D inside a never-selected
            // TabItem has no layout to render/frame a camera against yet.
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                    Dispatcher.InvokeAsync(Rebuild, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((MosaicDomeSurfaceView)d).Rebuild();

        // Overall scene scale - deliberately matching the old globe's SphereRadius=30 so switching
        // between this tab and the 2D Zenith Dome (whose own domeRadius is independently chosen in
        // pixel space) at least feels like a consistent "world size" rather than an arbitrary jump.
        private const double DomeRadius = 30.0;

        // The largest |value| in the current data maps to this height (see Height's remarks on
        // why the mapping is zero-anchored/linear rather than min-max-normalized) - comfortably
        // visible against DomeRadius without dwarfing the reference grid.
        private const double MaxHeightExtent = 12.0;

        private const double StemRadius = 0.3;
        private const double TipRadius = 0.8;
        private const int CylinderSegments = 8;
        private const int SphereThetaDiv = 10;
        private const int SpherePhiDiv = 6;
        private const int RingSegments = 64;
        private const double LabelOffset = DomeRadius * 0.12;
        private const double LabelHeight = DomeRadius * 0.07;
        private static readonly Vector3D LabelTextDirection = new(1, 0, 0);
        private static readonly Vector3D LabelUpDirection = new(0, 1, 0);

        /// <summary>
        /// Ground-plane (X,Y) position for a given Az/El, sharing the exact same projection as
        /// the 2D Zenith Dome's own Project() (MosaicViewModel.RenderDome): a linear zenith-angle
        /// radius (0 at the zenith center, DomeRadius at the horizon edge) and azimuth negated so
        /// the compass reads correctly for a naked-eye sky view (N away from the viewer at Az=0,
        /// E to one side, W to the other - see MosaicViewModel.RenderDome's own compass remarks).
        /// The reference grid sits at Z=0 (this method's own two coordinates); extrusion height
        /// is handled separately by Height, along Z, since it depends on the point's *value*, not
        /// its Az/El - see Rebuild's Point3D(x, y, height) construction.
        /// </summary>
        private static (double x, double y) DomeXY(double azDeg, double elDeg)
        {
            double r = Math.Clamp((90.0 - elDeg) / 90.0, 0.0, 1.0) * DomeRadius;
            double azRad = azDeg * Math.PI / 180.0;
            return (-r * Math.Sin(azRad), -r * Math.Cos(azRad));
        }

        private void Rebuild()
        {
            PointsVisual.Content = null;
            MeshVisual.Content = null;
            AxisVisual.Children.Clear();
            AxisVisual.Content = null;

            BuildReferenceGrid();

            var points = Points;
            if (points is null || points.Count == 0)
                return;

            double min = points.Min(p => p.Value);
            double max = points.Max(p => p.Value);
            double maxAbs = Math.Max(Math.Max(Math.Abs(min), Math.Abs(max)), 1e-9);

            // Zero-anchored/linear, not min-max-normalized: a value of 0 (0 km/s at the LSR-
            // corrected line center, or 0 dB - no excess above the cold-sky baseline) is a real,
            // physically meaningful reference in both metrics, so it belongs exactly on the Z=0
            // ground plane rather than wherever this session's own min/max happen to place it.
            // Height and colour share the same anchor for the same reason - see NormColorT.
            double Height(double v) => v / maxAbs * MaxHeightExtent;
            double NormColorT(double v) => Math.Clamp(0.5 + v / (2 * maxAbs), 0.0, 1.0);

            // The stems/dots and the fitted mesh are alternate readings of the same data - showing
            // both at once mostly just doubles up on the same shape (the mesh already passes
            // through every stem's own tip), so FitMesh hides the per-point geometry rather than
            // overlaying it. Skipping BuildStemsAndDots entirely when meshed also saves the work
            // of building potentially hundreds of cylinders/spheres nothing will show.
            if (FitMesh)
                BuildFittedMesh(points, Height, NormColorT);
            else
                BuildStemsAndDots(points, Height, NormColorT);

            // There IS a well-defined "whole object" here (unlike the old globe's dead-center
            // vantage point - see MosaicDomeSurfaceView.xaml's CameraMode="Inspect" remarks), so
            // re-fitting the camera on every data refresh is exactly the right default, not a
            // fight against the user's own position the way it was for the globe.
            Viewport.ZoomExtents(0);
        }

        /// <summary>
        /// One cylinder "stem" + sphere "tip" per point, merged into a single mesh (one draw
        /// call) with each point's own value baked in as a constant texture coordinate across its
        /// whole stem+tip - see AppendCylinder/AppendSphere's own remarks on why their normals are
        /// exact parametric ones rather than averaged.
        /// </summary>
        private void BuildStemsAndDots(IReadOnlyList<DomeSurfacePoint> points, Func<double, double> height, Func<double, double> normColorT)
        {
            var positions = new Point3DCollection();
            var indices = new Int32Collection();
            var texCoords = new PointCollection();
            var normals = new Vector3DCollection();

            foreach (var p in points)
            {
                var (x, y) = DomeXY(p.AzDeg, p.ElDeg);
                double h = height(p.Value);
                double t = normColorT(p.Value);

                AppendCylinder(positions, indices, texCoords, normals,
                    new Point3D(x, y, 0), new Point3D(x, y, h), StemRadius, CylinderSegments, t);
                AppendSphere(positions, indices, texCoords, normals,
                    new Point3D(x, y, h), TipRadius, SphereThetaDiv, SpherePhiDiv, t);
            }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = indices,
                TextureCoordinates = texCoords,
                Normals = normals
            };
            var material = new DiffuseMaterial(TranslucentGradientBrush);
            PointsVisual.Content = new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        /// <summary>
        /// Fits a Delaunay-triangulated translucent surface through each point's own extruded
        /// (x, height, z) position - i.e. the mesh vertices are each point's exact own center,
        /// never a grid-binned approximation of it, since Zenith Dome positions (live Az/El at
        /// one instant) aren't on a uniform grid the way GridBuilder's RA/Dec canvas is. Long
        /// edges (see DelaunayTriangulation.FilterLongEdges) are trimmed at a multiple of the
        /// actual median edge length, so a sparse/clustered session doesn't bridge distant,
        /// physically unrelated positions with an absurdly long, meaningless triangle - plain
        /// Delaunay triangulation always spans the full convex hull of its input otherwise.
        /// </summary>
        private void BuildFittedMesh(IReadOnlyList<DomeSurfacePoint> points, Func<double, double> height, Func<double, double> normColorT)
        {
            if (points.Count < 3)
                return;

            var pts2D = new List<DelaunayTriangulation.Point2D>(points.Count);
            var extruded = new List<Point3D>(points.Count);
            foreach (var p in points)
            {
                var (x, y) = DomeXY(p.AzDeg, p.ElDeg);
                pts2D.Add(new DelaunayTriangulation.Point2D(x, y));
                extruded.Add(new Point3D(x, y, height(p.Value)));
            }

            var triangles = DelaunayTriangulation.Triangulate(pts2D);
            if (triangles.Count == 0)
                return;

            double Dist(int i, int j)
            {
                double dx = pts2D[i].X - pts2D[j].X, dz = pts2D[i].Y - pts2D[j].Y;
                return Math.Sqrt(dx * dx + dz * dz);
            }

            var edgeLengths = new List<double>(triangles.Count * 3);
            foreach (var t in triangles)
            {
                edgeLengths.Add(Dist(t.A, t.B));
                edgeLengths.Add(Dist(t.B, t.C));
                edgeLengths.Add(Dist(t.C, t.A));
            }
            edgeLengths.Sort();
            double medianEdge = edgeLengths[edgeLengths.Count / 2];
            triangles = DelaunayTriangulation.FilterLongEdges(triangles, pts2D, Math.Max(medianEdge * 4, 1e-6));
            if (triangles.Count == 0)
                return;

            var meshPositions = new Point3DCollection(extruded);
            var meshTexCoords = new PointCollection(points.Count);
            foreach (var p in points)
                meshTexCoords.Add(new Point(normColorT(p.Value), 0));

            var meshIndices = new Int32Collection(triangles.Count * 3);
            foreach (var t in triangles)
            {
                meshIndices.Add(t.A);
                meshIndices.Add(t.B);
                meshIndices.Add(t.C);
            }

            // General irregular surface (not a known parametric shape like the stems/tips above),
            // so normals need the same face-normal-averaging technique the old globe mesh used -
            // see its own remarks on why WPF's automatic normal generation isn't reliable here.
            var normalSums = new Vector3D[meshPositions.Count];
            for (int i = 0; i < meshIndices.Count; i += 3)
            {
                int i0 = meshIndices[i], i1 = meshIndices[i + 1], i2 = meshIndices[i + 2];
                var p0 = meshPositions[i0];
                var faceNormal = Vector3D.CrossProduct(meshPositions[i1] - p0, meshPositions[i2] - p0);
                normalSums[i0] += faceNormal;
                normalSums[i1] += faceNormal;
                normalSums[i2] += faceNormal;
            }
            var meshNormals = new Vector3DCollection(meshPositions.Count);
            foreach (var sum in normalSums)
            {
                var n = sum;
                if (n.LengthSquared > 1e-12) n.Normalize();
                else n = new Vector3D(0, 1, 0); // untouched vertex (shouldn't happen with 3+ points) - "up" is the only sane default on a mostly-flat terrain
                meshNormals.Add(n);
            }

            var mesh = new MeshGeometry3D
            {
                Positions = meshPositions,
                TriangleIndices = meshIndices,
                TextureCoordinates = meshTexCoords,
                Normals = meshNormals
            };
            var material = new DiffuseMaterial(TranslucentGradientBrush);
            MeshVisual.Content = new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        /// <summary>
        /// Fixed reference geometry, independent of any data - altitude rings every 15deg
        /// (El=0's ring is the horizon boundary), azimuth spokes every 30deg, and the 8 principal
        /// compass labels, all sitting on the Z=0 ground plane (X-Y) exactly matching the 2D
        /// Zenith Dome's own fixed conventions (see MosaicViewModel.RenderDome) - extrusion height
        /// is along Z instead, per MosaicDomeSurfaceView.xaml's UpDirection="0,0,1".
        /// </summary>
        private void BuildReferenceGrid()
        {
            var linePoints = new Point3DCollection();

            for (double el = 0; el < 90; el += 15)
            {
                double r = (90.0 - el) / 90.0 * DomeRadius;
                for (int i = 0; i < RingSegments; i++)
                {
                    double a0 = 2 * Math.PI * i / RingSegments;
                    double a1 = 2 * Math.PI * (i + 1) / RingSegments;
                    linePoints.Add(new Point3D(-r * Math.Sin(a0), -r * Math.Cos(a0), 0));
                    linePoints.Add(new Point3D(-r * Math.Sin(a1), -r * Math.Cos(a1), 0));
                }
            }

            var labels = new Model3DGroup();
            string[] compassPoints = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            for (int i = 0; i < 12; i++)
            {
                double az = i * 30.0;
                var (ex, ey) = DomeXY(az, 0.0);
                linePoints.Add(new Point3D(0, 0, 0));
                linePoints.Add(new Point3D(ex, ey, 0));
            }
            for (int i = 0; i < compassPoints.Length; i++)
            {
                double az = i * 45.0;
                double azRad = az * Math.PI / 180.0;
                double labelR = DomeRadius + LabelOffset;
                var center = new Point3D(-labelR * Math.Sin(azRad), -labelR * Math.Cos(azRad), LabelHeight);
                labels.Children.Add(TextCreator.CreateTextLabelModel3D(
                    compassPoints[i], Brushes.DimGray, isDoubleSided: true, height: LabelHeight,
                    center: center, textDirection: LabelTextDirection, updirection: LabelUpDirection));
            }

            AxisVisual.Children.Add(new LinesVisual3D { Points = linePoints, Color = Colors.Gray, Thickness = 0.6 });
            AxisVisual.Content = labels;
        }

        /// <summary>
        /// Appends one cylinder's side geometry (no end caps - the base sits invisibly on/below
        /// the reference plane, the top is covered by the sphere tip) into shared mesh buffers,
        /// all vertices tagged with the same texValue so the whole stem renders as one flat
        /// colour matching its own point's value, not a gradient wrapped around it. Normals are
        /// the exact radial-from-axis direction (unit length by construction, side/up being an
        /// orthonormal basis) rather than averaged afterward - always correct for a true
        /// cylinder, viewed from any external angle.
        /// </summary>
        private static void AppendCylinder(
            Point3DCollection positions, Int32Collection indices, PointCollection texCoords, Vector3DCollection normals,
            Point3D p0, Point3D p1, double radius, int segments, double texValue)
        {
            Vector3D axis = p1 - p0;
            if (axis.LengthSquared < 1e-12)
                return;
            axis.Normalize();

            Vector3D arbitrary = Math.Abs(axis.Y) < 0.99 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
            Vector3D side = Vector3D.CrossProduct(axis, arbitrary);
            side.Normalize();
            Vector3D up = Vector3D.CrossProduct(axis, side);

            int baseIndex = positions.Count;
            for (int i = 0; i <= segments; i++)
            {
                double theta = 2 * Math.PI * i / segments;
                var radial = side * Math.Cos(theta) + up * Math.Sin(theta);

                positions.Add(p0 + radial * radius);
                normals.Add(radial);
                texCoords.Add(new Point(texValue, 0));

                positions.Add(p1 + radial * radius);
                normals.Add(radial);
                texCoords.Add(new Point(texValue, 0));
            }

            for (int i = 0; i < segments; i++)
            {
                int i0 = baseIndex + i * 2;
                int i1 = i0 + 1;
                int i2 = baseIndex + (i + 1) * 2;
                int i3 = i2 + 1;
                indices.Add(i0); indices.Add(i2); indices.Add(i1);
                indices.Add(i1); indices.Add(i2); indices.Add(i3);
            }
        }

        /// <summary>
        /// Appends one UV sphere into shared mesh buffers, same texValue-tagging convention as
        /// AppendCylinder. Normals are the exact radial-from-center direction - always correct
        /// for a true sphere.
        /// </summary>
        private static void AppendSphere(
            Point3DCollection positions, Int32Collection indices, PointCollection texCoords, Vector3DCollection normals,
            Point3D center, double radius, int thetaDiv, int phiDiv, double texValue)
        {
            int baseIndex = positions.Count;
            for (int p = 0; p <= phiDiv; p++)
            {
                double phi = Math.PI * p / phiDiv;
                double y = Math.Cos(phi);
                double r = Math.Sin(phi);
                for (int t = 0; t <= thetaDiv; t++)
                {
                    double theta = 2 * Math.PI * t / thetaDiv;
                    var normal = new Vector3D(r * Math.Cos(theta), y, r * Math.Sin(theta));
                    positions.Add(center + normal * radius);
                    normals.Add(normal);
                    texCoords.Add(new Point(texValue, 0));
                }
            }

            int rowSize = thetaDiv + 1;
            for (int p = 0; p < phiDiv; p++)
            {
                for (int t = 0; t < thetaDiv; t++)
                {
                    int i0 = baseIndex + p * rowSize + t;
                    int i1 = i0 + 1;
                    int i2 = i0 + rowSize;
                    int i3 = i2 + 1;
                    indices.Add(i0); indices.Add(i2); indices.Add(i1);
                    indices.Add(i1); indices.Add(i2); indices.Add(i3);
                }
            }
        }

        /// <summary>
        /// Same diverging blue-gray-red ramp as HeatmapImageBuilder/the old globe, as a
        /// LinearGradientBrush rather than a raster ImageBrush - see the old MosaicSurfaceView's
        /// own history for why a raster texture silently failed to render as a 3D material here.
        /// </summary>
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

        // ~85% opaque - visible enough to read colour clearly, translucent enough that the Z=0
        // reference grid/labels remain legible when a stem/dot/mesh face sits between it and the
        // camera (routine for a metric whose range straddles zero).
        private static readonly LinearGradientBrush TranslucentGradientBrush = BuildGradientBrush(215);
    }
}
