namespace RASTA.Processing.Gridding
{
    /// <summary>
    /// A plain 2D Bowyer-Watson Delaunay triangulation, used by MosaicDomeSurfaceView to fit a
    /// mesh through a scattered set of dome-projected points (X/Z ground-plane position, with
    /// each point's own extruded height carried separately) rather than a uniform grid -
    /// GridBuilder's own quad-mesh approach only works because its grid is already uniform;
    /// Zenith Dome positions (live Az/El at one instant) generally aren't. Pure algorithm, no
    /// WPF/3D dependency, so it lives alongside GridBuilder in RASTA.Processing rather than in
    /// the WPF-facing view itself.
    ///
    /// Textbook incremental Bowyer-Watson: start from one triangle big enough to contain every
    /// input point (the "super-triangle"), insert points one at a time - for each, find every
    /// triangle whose circumcircle contains the new point ("bad" triangles), remove them, and
    /// re-triangulate the polygonal hole they leave by connecting the new point to every edge on
    /// that hole's boundary. O(n) triangles are visited per insertion in the worst case (no
    /// spatial index), which is fine for the few hundred to low thousands of points a mosaic
    /// session realistically produces - not written for very large point counts.
    /// </summary>
    public static class DelaunayTriangulation
    {
        public readonly record struct Point2D(double X, double Y);
        public readonly record struct Triangle(int A, int B, int C);

        /// <summary>
        /// Triangulates the given points, returning triangles as indices into the original
        /// <paramref name="points"/> list (the super-triangle's own extra vertices are never
        /// exposed). Returns an empty list for fewer than 3 points - nothing to triangulate.
        /// </summary>
        public static List<Triangle> Triangulate(IReadOnlyList<Point2D> points)
        {
            int n = points.Count;
            var result = new List<Triangle>();
            if (n < 3)
                return result;

            // A triangle comfortably larger than every input point, centered on their bounding
            // box, so every real point is guaranteed to fall strictly inside it.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            double spanX = maxX - minX, spanY = maxY - minY;
            double deltaMax = Math.Max(spanX, spanY) * 10 + 1;
            double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;

            var pts = new List<Point2D>(n + 3);
            pts.AddRange(points);
            int superA = n, superB = n + 1, superC = n + 2;
            pts.Add(new Point2D(midX - 2 * deltaMax, midY - deltaMax));
            pts.Add(new Point2D(midX, midY + 2 * deltaMax));
            pts.Add(new Point2D(midX + 2 * deltaMax, midY - deltaMax));

            var triangles = new List<Triangle> { new(superA, superB, superC) };

            for (int pIdx = 0; pIdx < n; pIdx++)
            {
                var p = pts[pIdx];

                var badTriangles = new List<Triangle>();
                foreach (var t in triangles)
                {
                    if (InCircumcircle(pts[t.A], pts[t.B], pts[t.C], p))
                        badTriangles.Add(t);
                }

                // The boundary of the union of bad triangles: an edge belongs to it only if no
                // *other* bad triangle also has that same edge (an edge shared between two bad
                // triangles is interior to the hole, not part of its boundary).
                var boundary = new List<(int, int)>();
                foreach (var t in badTriangles)
                {
                    var edges = new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) };
                    foreach (var (e1, e2) in edges)
                    {
                        bool sharedWithAnotherBadTriangle = false;
                        foreach (var other in badTriangles)
                        {
                            if (other.Equals(t)) continue;
                            if (HasEdge(other, e1, e2)) { sharedWithAnotherBadTriangle = true; break; }
                        }
                        if (!sharedWithAnotherBadTriangle)
                            boundary.Add((e1, e2));
                    }
                }

                triangles.RemoveAll(t => badTriangles.Contains(t));

                foreach (var (e1, e2) in boundary)
                    triangles.Add(new Triangle(e1, e2, pIdx));
            }

            // Drop every triangle still touching one of the super-triangle's own 3 vertices.
            foreach (var t in triangles)
            {
                if (t.A < n && t.B < n && t.C < n)
                    result.Add(t);
            }
            return result;
        }

        /// <summary>
        /// Drops any triangle with an edge longer than <paramref name="maxEdgeLength"/> - plain
        /// Delaunay triangulation always spans the full convex hull of the input, which for a
        /// sparse/clustered point set (e.g. several separate mosaic sessions' worth of positions
        /// landing far apart on the sky) bridges distant clusters with long, physically
        /// meaningless triangles rather than leaving a genuine gap. Call after Triangulate with a
        /// threshold sized to the data (e.g. a multiple of the typical/median edge length).
        /// </summary>
        public static List<Triangle> FilterLongEdges(List<Triangle> triangles, IReadOnlyList<Point2D> points, double maxEdgeLength)
        {
            double maxSq = maxEdgeLength * maxEdgeLength;
            double DistSq(int i, int j)
            {
                double dx = points[i].X - points[j].X, dy = points[i].Y - points[j].Y;
                return dx * dx + dy * dy;
            }

            var result = new List<Triangle>(triangles.Count);
            foreach (var t in triangles)
            {
                if (DistSq(t.A, t.B) <= maxSq && DistSq(t.B, t.C) <= maxSq && DistSq(t.C, t.A) <= maxSq)
                    result.Add(t);
            }
            return result;
        }

        private static bool HasEdge(Triangle t, int v1, int v2)
        {
            bool has1 = t.A == v1 || t.B == v1 || t.C == v1;
            bool has2 = t.A == v2 || t.B == v2 || t.C == v2;
            return has1 && has2;
        }

        /// <summary>
        /// True if point p falls strictly inside the circumcircle of triangle (a,b,c), using the
        /// standard determinant test - sign-corrected for the triangle's own winding (CCW vs CW),
        /// since the raw determinant's sign flips with orientation and a/b/c's winding isn't
        /// controlled by this algorithm (the super-triangle and every hole re-triangulation could
        /// produce either).
        /// </summary>
        private static bool InCircumcircle(Point2D a, Point2D b, Point2D c, Point2D p)
        {
            double ax = a.X - p.X, ay = a.Y - p.Y;
            double bx = b.X - p.X, by = b.Y - p.Y;
            double cx = c.X - p.X, cy = c.Y - p.Y;

            double det =
                (ax * ax + ay * ay) * (bx * cy - cx * by) -
                (bx * bx + by * by) * (ax * cy - cx * ay) +
                (cx * cx + cy * cy) * (ax * by - bx * ay);

            double orientation = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            return orientation > 0 ? det > 0 : det < 0;
        }
    }
}
