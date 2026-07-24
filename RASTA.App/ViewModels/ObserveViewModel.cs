using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using RASTA.Core.Capture;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;
using RASTA.Processing.Capture;
using System.Windows;

namespace RASTA.App.ViewModels;

public partial class ObserveViewModel : ObservableObject
{
    private readonly ITelescopeMount _mount;
    private readonly SdrRawCaptureService _capture;
    private readonly ISdrDevice _device;

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
        ISdrDevice device,
        SdrRawCaptureService capture)
    {
        _mount = mount;
        _capture = capture;
        _device = device;
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

    // -------------------------------------------------------
    // Capture observation
    // -------------------------------------------------------

    [ObservableProperty]
    private string? lastCapturePath;

    [RelayCommand]
    private async Task TestRawCaptureAsync()
    {
        try
        {
            // Example hydrogen-line test capture
            double freqMHz = 1420.4058;
            double sampleRateHz = 2_048_000;
            double gainDb = _device.SupportedGainsDb.Last();
            TimeSpan dwell = TimeSpan.FromSeconds(15);

            string file = await _capture.CaptureRawIqToFitsAsync(
                frequencyHz: freqMHz * 1_000_000,
                sampleRateHz: sampleRateHz,
                gainDb: gainDb,
                dwell: dwell,
                ct: CancellationToken.None);

            LastCapturePath = file;

            MessageBox.Show($"RAW IQ saved:\n{file}", "Capture Complete");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Capture Error");
        }
    }
}
