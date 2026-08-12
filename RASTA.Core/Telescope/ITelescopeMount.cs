using RASTA.Core.Telescope;

public interface ITelescopeMount
{
    bool IsConnected { get; }

    // Session-specific
    CoordinateMode Mode { get; }
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
    Task<double> GetRightAscensionHoursAsync();
    Task<double> GetDeclinationDegAsync();
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
    Task SlewToRaDecAsync(double raHours, double decDeg);
    Task SlewToAzAltAsync(double azDeg, double altDeg);
    Task AbortSlewAsync();

    // Calibration
    Task<CoordinateMode> DetectCoordinateModeAsync();
}
