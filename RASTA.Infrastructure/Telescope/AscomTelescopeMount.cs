using RASTA.Core.Capture;
using RASTA.Core.Telescope;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RASTA.Infrastructure.Telescope;
public class AscomTelescopeMount : ITelescopeMount
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOptions;

    public AscomTelescopeMount(string baseUrl)
    {
        _baseUrl = baseUrl; // e.g. http://localhost:11111/api/v1/telescope/0
        _client = new HttpClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task ConnectAsync()
    {
        await _client.PutAsync($"{_baseUrl}/connected", null);
    }

    public async Task DisconnectAsync()
    {
        await _client.PutAsync($"{_baseUrl}/connected", new StringContent("false"));
    }

    public async Task<CoordinateMode> DetectCoordinateModeAsync()
    {
        try
        {
            var url = $"{_baseUrl}/alignmentmode";

            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<AlpacaResponse<int>>(json, _jsonOptions);

            if (result is null)
                return CoordinateMode.Equatorial;

            return result.Value switch
            {
                0 => CoordinateMode.AltAz,
                1 => CoordinateMode.Equatorial,
                2 => CoordinateMode.Equatorial,
                _ => CoordinateMode.Equatorial
            };
        }
        catch
        {
            return CoordinateMode.Equatorial;
        }
    }


    public async Task SlewToAzElAsync(double azDeg, double elDeg)
    {
        await _client.PutAsync(
            $"{_baseUrl}/slewtoazalt?Azimuth={azDeg}&Altitude={elDeg}",
            null);
    }

    public async Task<TelescopePosition> GetPositionAsync()
    {
        var url = $"{_baseUrl}/azimuthaltitude";

        var result = await _client.GetFromJsonAsync<AlpacaResponse<TelescopePosition>>(url, _jsonOptions);

        return result?.Value ?? new TelescopePosition();
    }


    public async Task AbortSlewAsync()
    {
        await _client.PutAsync($"{_baseUrl}/abortslew", null);
    }

    public async Task SlewToRaDecAsync(double raHours, double decDeg)
    {
        await _client.PutAsync(
            $"{_baseUrl}/slewtocoordinates?RightAscension={raHours}&Declination={decDeg}",
            null);
    }

    public async Task SlewToTargetAsync(TargetPoint target)
    {
        if (target.Mode == CoordinateMode.AltAz)
        {
            await SlewToAzElAsync(target.AzimuthDeg, target.ElevationDeg);
        }
        else
        {
            await SlewToRaDecAsync(target.RightAscensionHours, target.DeclinationDeg);
        }
    }

}
