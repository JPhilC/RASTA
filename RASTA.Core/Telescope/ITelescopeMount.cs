using RASTA.Core.Telescope;

public interface ITelescopeMount
{
    bool IsConnected { get; }

    // Session-specific
    CoordinateMode Mode { get; }

    // RA/Dec epoch (issue #38). Every RA/Dec crossing this interface is J2000; the
    // implementation converts to/from whatever the mount itself uses.

    /// <summary>What the mount reported on connect, or null if it doesn't implement it.</summary>
    EquatorialCoordinateSystem? ReportedEquatorialSystem { get; }

    /// <summary>Assumed when the mount reports Other or doesn't implement the property.</summary>
    EquatorialCoordinateSystem FallbackEquatorialSystem { get; set; }

    /// <summary>The system actually used for conversion - never Other.</summary>
    EquatorialCoordinateSystem EffectiveEquatorialSystem { get; }

    double SiteLatitudeDeg { get; }
    double SiteLongitudeDeg { get; }
    double SiteElevationM { get; }

    // Connection
    Task ConnectAsync();
    Task DisconnectAsync();

    // Marks the mount locally disconnected without any live I/O - used when a caller
    // has already learned (e.g. a live poll throwing) that the link is down, so a
    // graceful DisconnectAsync() round-trip would just hang or fail again.
    void MarkDisconnected();

    // Site management
    Task SetSiteLatitudeAsync(double latitudeDeg);
    Task SetSiteLongitudeAsync(double longitudeDeg);
    Task SetSiteElevationAsync(double elevationM);

    // Tracking
    Task<bool> GetTrackingAsync();
    Task SetTrackingAsync(bool enabled);

    Task<int> GetTrackingRateAsync();
    Task SetTrackingRateAsync(int rate);
    Task SetSiderealTrackingAsync();

    // State queries
    /// <summary>Current pointing, J2000 whatever the mount itself uses.</summary>
    Task<(double raHours, double decDeg)> GetRaDecAsync();
    Task<double> GetAzimuthDegAsync();
    Task<double> GetAltitudeDegAsync();
    Task<bool> GetSlewingAsync();
    Task<bool> GetAtHomeAsync();
    Task<bool> GetAtParkAsync();

    Task<bool> GetCanSetTrackingAsync();

    Task<bool> GetCanFindHomeAsync();

    Task ParkAsync();
    Task UnParkAsync();

    Task FindHomeAsync();

    // Slewing
    /// <summary>Slews to a J2000 RA/Dec (converted to the mount's own system first).</summary>
    Task SlewToRaDecAsync(double raHours, double decDeg);
    Task SlewToAzAltAsync(double azDeg, double altDeg);
    Task AbortSlewAsync();

    // Calibration
    Task<CoordinateMode> DetectCoordinateModeAsync();
}
