namespace RASTA.Core.Calibration
{
    /// <summary>
    /// One candidate "cold sky" pointing offered to the user when running a new calibration -
    /// a position expected to be clear of strong Galactic HI emission (see ColdSkyLocator in
    /// RASTA.Processing) that the mount can slew to for the baseline capture. Carries both
    /// coordinate systems, exactly like TargetPoint/FitsFileMetaData do, so the choice can be
    /// used directly for slewing (Az/Alt) and recorded on the FITS header (both).
    /// </summary>
    public sealed record ColdSkyCandidate(
        double AzimuthDeg,
        double ElevationDeg,
        double RightAscensionHours,
        double DeclinationDeg,
        double GalacticLongitudeDeg,
        double GalacticLatitudeDeg);
}
