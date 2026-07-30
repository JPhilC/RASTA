using RASTA.Core.Telescope;
using System.Globalization;

namespace RASTA.Infrastructure.Telescope;

public class AscomTelescopeMount : ITelescopeMount
{
    private readonly AscomAlpacaClient _client;
    private int _deviceNumber = 0;

    public bool IsConnected { get; private set; }
    public CoordinateMode Mode { get; private set; }

    public double SiteLatitudeDeg { get; private set; }
    public double SiteLongitudeDeg { get; private set; }
    public double SiteElevationM { get; private set; }

    public AscomTelescopeMount(AscomAlpacaClient client)
    {
        _client = client;
    }

    // -------------------------
    // Session configuration
    // -------------------------

    public void SetBaseUrl(string baseUrl)
    {
        // baseUrl should be like: "http://127.0.0.1:11111/api/v1/telescope"
        // We append the device number when building endpoint URLs.
        _client.BaseUrl = $"{baseUrl}/{_deviceNumber}";
    }

    public void SetDeviceNumber(int deviceNumber)
    {
        _deviceNumber = deviceNumber;

        // If BaseUrl was already set, update it to include the new device number.
        if (!string.IsNullOrWhiteSpace(_client.BaseUrl))
        {
            // Strip any trailing /<number> and re-append
            var idx = _client.BaseUrl.LastIndexOf('/');
            if (idx > 0)
            {
                var root = _client.BaseUrl[..idx];
                _client.BaseUrl = $"{root}/{_deviceNumber}";
            }
        }
    }

    // -------------------------
    // Connection
    // -------------------------
    public async Task ConnectAsync()
    {
        await _client.PutAsync("connected", ("Connected", "true"));
        IsConnected = true;

        // Retrieve site values
        SiteLatitudeDeg = await _client.GetAsync<double>("sitelatitude");
        SiteLongitudeDeg = await _client.GetAsync<double>("sitelongitude");
        SiteElevationM = await _client.GetAsync<double>("siteelevation");

        // Detect mode
        Mode = await DetectCoordinateModeAsync();
    }

    public async Task DisconnectAsync()
    {
        await _client.PutAsync("connected", ("Connected", "false"));
        IsConnected = false;
    }

    public Task SetSiteLatitudeAsync(double latitudeDeg)
    {
        SiteLatitudeDeg = latitudeDeg;
        return _client.PutAsync("sitelatitude", ("SiteLatitude", latitudeDeg.ToString(CultureInfo.InvariantCulture)));
    }

    public Task SetSiteLongitudeAsync(double longitudeDeg)
    {
        SiteLongitudeDeg = longitudeDeg;
        return _client.PutAsync("sitelongitude", ("SiteLongitude", longitudeDeg.ToString(CultureInfo.InvariantCulture)));
    }

    public Task SetSiteElevationAsync(double elevationM)
    {
        SiteElevationM = elevationM;
        return _client.PutAsync("siteelevation", ("SiteElevation", elevationM.ToString(CultureInfo.InvariantCulture)));
    }

    public Task SetTrackingAsync(bool enabled)
    {
        return _client.PutAsync("tracking",
            ("Tracking", enabled ? "true" : "false"));
    }

    public Task SetTrackingRateAsync(int rate)
    {
        return _client.PutAsync("trackingrate",
            ("TrackingRate", rate.ToString(CultureInfo.InvariantCulture)));
    }

    public Task ParkAsync() => _client.PutAsync("park");

    public Task UnParkAsync() => _client.PutAsync("unpark");

    public Task FindHomeAsync() => _client.PutAsync("findhome");

    public async Task SetSiderealTrackingAsync()
    {
        await SetTrackingAsync(true);
        await SetTrackingRateAsync(0); // 0 = Sidereal
    }

    public Task<double> GetRightAscensionHoursAsync() => _client.GetAsync<double>("rightascension");
    public Task<double> GetDeclinationDegAsync() => _client.GetAsync<double>("declination");
    public Task<double> GetAzimuthDegAsync() => _client.GetAsync<double>("azimuth");
    public Task<double> GetAltitudeDegAsync() => _client.GetAsync<double>("altitude");
    public Task<bool> GetTrackingAsync() => _client.GetAsync<bool>("tracking");
    public Task<bool> GetSlewingAsync() => _client.GetAsync<bool>("slewing");
    public Task<bool> GetAtHomeAsync() => _client.GetAsync<bool>("athome");
    public Task<bool> GetAtParkAsync() => _client.GetAsync<bool>("atpark");
    public Task<int> GetTrackingRateAsync() => _client.GetAsync<int>("trackingrate");

    public Task<bool> GetCanSetTrackingAsync() => _client.GetAsync<bool>("cansettracking");

    public Task<bool> GetCanFindHomeAsync() => _client.GetAsync<bool>("canfindhome");

    public Task SlewToRaDecAsync(double raHours, double decDeg)
    {
        return _client.PutAsync("slewtocoordinates",
            ("RightAscension", raHours.ToString(CultureInfo.InvariantCulture)),
            ("Declination", decDeg.ToString(CultureInfo.InvariantCulture)));
    }

    public Task SlewToAzAltAsync(double azDeg, double altDeg)
    {
        return _client.PutAsync("slewtoaltaz",
            ("Azimuth", azDeg.ToString(CultureInfo.InvariantCulture)),
            ("Altitude", altDeg.ToString(CultureInfo.InvariantCulture)));
    }

    public Task AbortSlewAsync() => _client.PutAsync("abortslew");

    public async Task<CoordinateMode> DetectCoordinateModeAsync()
    {
        var alignment = await _client.GetAsync<int>("alignmentmode");

        return alignment switch
        {
            0 => CoordinateMode.AltAz,
            1 => CoordinateMode.Equatorial,
            2 => CoordinateMode.Equatorial,
            _ => CoordinateMode.Equatorial
        };
    }
}
