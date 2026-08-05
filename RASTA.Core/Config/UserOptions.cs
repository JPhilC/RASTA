using CommunityToolkit.Mvvm.ComponentModel;

namespace RASTA.Core.Config
{
    public partial class UserOptions: ObservableObject
    {
        [ObservableProperty]
        private string captureFolder = "C:\\RAW\\RASTA\\Captures";

        [ObservableProperty]
        private string plansFolder = "C:\\RAW\\RASTA\\Plans";

        [ObservableProperty]
        private double defaultCentreFrequencyHz = 1_420_700_000;

        [ObservableProperty]
        private double defaultBandwidthHz = 2_400_000;

        [ObservableProperty]
        private int defaultFftSize = 4096;

    }
}
