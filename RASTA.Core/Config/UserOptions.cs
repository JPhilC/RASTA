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

        // Site settings are now editable in SettingsViewModel without a mount attached (see
        // its remarks on reconciling against a connected mount's own values) - persisted here
        // so RASTA remembers the last-confirmed site across app restarts instead of resetting
        // to 0/0/0 every launch, which would otherwise make the mount-vs-RASTA reconciliation
        // prompt fire on every single connect regardless of whether anything actually changed.
        [ObservableProperty]
        private double siteLatitudeDeg;

        [ObservableProperty]
        private double siteLongitudeDeg;

        [ObservableProperty]
        private double siteElevationM;

    }
}
