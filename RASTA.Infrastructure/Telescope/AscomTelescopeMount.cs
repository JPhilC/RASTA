using RASTA.Core.Capture;
using RASTA.Core.Telescope;
using System.Net.Http;
using System.Net.Http.Json;

namespace RASTA.Infrastructure.Telescope;
public class AscomTelescopeMount : ITelescopeMount
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public AscomTelescopeMount(string baseUrl)
    {
        _baseUrl = baseUrl; // e.g. http://localhost:11111/api/v1/telescope/0
        _client = new HttpClient();
    }

    public async Task ConnectAsync()
    {
        await _client.PutAsync($"{_baseUrl}/connected", null);
    }

    public async Task DisconnectAsync()
    {
        await _client.PutAsync($"{_baseUrl}/connected", new StringContent("false"));
    }

    public async Task SlewToAzElAsync(double azDeg, double elDeg)
    {
        await _client.PutAsync(
            $"{_baseUrl}/slewtoazalt?Azimuth={azDeg}&Altitude={elDeg}",
            null);
    }

    public async Task<TelescopePosition> GetPositionAsync()
    {
        return await _client.GetFromJsonAsync<TelescopePosition>(
            $"{_baseUrl}/azimuthaltitude");
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
