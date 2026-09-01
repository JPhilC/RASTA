namespace RASTA.Core.Planning
{
    /// <summary>
    /// One vertex of a freeform region drawn on the Plan view's sky map (see
    /// PlanViewModel's region-drawing commands and SweepPlanner.BuildRegionGrid, which turns
    /// a closed loop of these into a coverage grid). Always equatorial (RA/Dec) - region
    /// drawing is Equatorial-plan-only, unlike TargetRange's Range mode which also supports
    /// AltAz. RaHours/DecDeg rather than a generic X/Y pair to match TargetRange's own
    /// RAStartHours/DecStartDeg convention.
    /// </summary>
    public class RegionVertex
    {
        public double RaHours { get; set; }
        public double DecDeg { get; set; }

        public RegionVertex() { }

        public RegionVertex(double raHours, double decDeg)
        {
            RaHours = raHours;
            DecDeg = decDeg;
        }
    }
}
