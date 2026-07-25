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

    Task ParkAsync();
    Task UnParkAsync();

    // Slewing
    Task SlewToRaDecAsync(double raHours, double decDeg);
    Task SlewToAzAltAsync(double azDeg, double altDeg);
    Task AbortSlewAsync();

    // Calibration
    Task<CoordinateMode> DetectCoordinateModeAsync();
}
