using RASTA.Core.Capture;

namespace RASTA.Core.Telescope;

public interface ITelescopeMount
{
    Task ConnectAsync();
    Task DisconnectAsync();
    Task SlewToAzElAsync(double azDeg, double elDeg);
    Task SlewToRaDecAsync(double raHours, double decDeg);
    Task SlewToTargetAsync(TargetPoint target);
    Task AbortSlewAsync();
    Task<TelescopePosition> GetPositionAsync();
}


