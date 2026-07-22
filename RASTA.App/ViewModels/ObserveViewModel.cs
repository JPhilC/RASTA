using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Capture;
using RASTA.Core.Telescope;
using RASTA.Processing.Capture;

namespace RASTA.App.ViewModels;

public partial class ObserveViewModel : ObservableObject
{
    private readonly ITelescopeMount _mount;
    private readonly ObservationCaptureService _capture;

    [ObservableProperty]
    private ObservationRecord? lastObservation;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private double currentAzDeg;

    [ObservableProperty]
    private double currentAltDeg;

    [ObservableProperty]
    private double currentRaHours;

    [ObservableProperty]
    private double currentDecDeg;

    [ObservableProperty]
    private bool isTracking;

    [ObservableProperty]
    private bool isSlewing;

    public ObserveViewModel(
        ITelescopeMount mount,
        ObservationCaptureService capture)
    {
        _mount = mount;
        _capture = capture;
    }

    // -------------------------------------------------------
    // Refresh mount state
    // -------------------------------------------------------

    [RelayCommand]
    private async Task RefreshStateAsync()
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;

            // Always read both coordinate systems
            currentAzDeg = await _mount.GetAzimuthDegAsync();
            currentAltDeg = await _mount.GetAltitudeDegAsync();

            currentRaHours = await _mount.GetRightAscensionHoursAsync();
            currentDecDeg = await _mount.GetDeclinationDegAsync();

            isTracking = await _mount.GetTrackingAsync();
            isSlewing = await _mount.GetSlewingAsync();
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Slew to a specific target point
    // -------------------------------------------------------

    [RelayCommand]
    private async Task SlewToTargetAsync(TargetPoint target)
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            if (target.Mode == CoordinateMode.Equatorial)
                await _mount.SlewToRaDecAsync(target.RightAscensionHours, target.DeclinationDeg);
            else
                await _mount.SlewToAzAltAsync(target.AzimuthDeg, target.ElevationDeg);
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Slew to a fixed Az/El (AltAz mode)
    // -------------------------------------------------------

    private async Task SlewToAzAltAsync(double azDeg, double altDeg)
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            await _mount.SlewToAzAltAsync(azDeg, altDeg);
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Slew to RA/Dec (Equatorial mode)
    // -------------------------------------------------------

    private async Task SlewToRaDecAsync(double raHours, double decDeg)
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            await _mount.SlewToRaDecAsync(raHours, decDeg);
        }
        finally
        {
            isBusy = false;
        }
    }

    // -------------------------------------------------------
    // Abort Slew
    // -------------------------------------------------------

    [RelayCommand]
    private async Task AbortSlewAsync()
    {
        if (!_mount.IsConnected)
            return;

        try
        {
            isBusy = true;
            await _mount.AbortSlewAsync();
        }
        finally
        {
            isBusy = false;
        }
    }

    //// -------------------------------------------------------
    //// Capture observation
    //// -------------------------------------------------------

    //[RelayCommand]
    //private async Task StartObservationAsync()
    //{
    //    if (!_mount.IsConnected)
    //        return;

    //    try
    //    {
    //        isBusy = true;

    //        // Read current pointing
    //        var az = await _mount.GetAzimuthDegAsync();
    //        var el = await _mount.GetAltitudeDegAsync();

    //        var target = TargetPoint.FromAzEl(_mount.Mode, az, el);

    //        lastObservation = await _capture.CaptureAsync(
    //            target,
    //            TimeSpan.FromSeconds(30),
    //            CancellationToken.None);
    //    }
    //    finally
    //    {
    //        isBusy = false;
    //    }
    //}
}
