using CommunityToolkit.Mvvm.ComponentModel;

namespace RASTA.Core.Sdr
{

    public partial class SdrState : ObservableObject
    {
        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private IReadOnlyList<SdrDeviceDescriptor>? devices;

        [ObservableProperty]
        private SdrDeviceDescriptor? selectedDevice;

        [ObservableProperty]
        private IReadOnlyList<double>? supportedGains;

        [ObservableProperty]
        private string? tunerType;

        [ObservableProperty]
        private double? actualFrequencyHz;

        [ObservableProperty]
        private double? actualSampleRateHz;

        [ObservableProperty]
        private double? actualGainDb;
    }
}