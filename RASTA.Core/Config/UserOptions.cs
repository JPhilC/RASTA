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
        private double defaultCentreFrequencyHz = 1_420_405_800;

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

        // Antenna dish diameter - see AntennaUtils.ComputeBeamwidthDeg, which PlanViewModel
        // uses to suggest a default AngularSeparationDeg for a new plan. Editable in Prepare's
        // Site Settings panel alongside the site lat/lon/elevation above, same
        // "persists across restarts, no mount needed" treatment. Defaults to RASTA's own
        // reference antenna (a 1.4m prime-focus dish).
        [ObservableProperty]
        private double dishDiameterM = 1.4;

        // Focal length - not used by ComputeBeamwidthDeg itself (see its remarks: the standard
        // 70*wavelength/diameter estimate already assumes a feed reasonably well-matched to the
        // dish, and refining that per-dish needs the feed's own illumination pattern, not just
        // f/D), but stored for context (SettingsViewModel.FocalRatio, shown next to Beamwidth so
        // you can sanity-check how far your dish sits from the ~0.35-0.5 range that assumption
        // targets) and for a future antenna-gain estimate, which does need f/D directly.
        [ObservableProperty]
        private double focalLengthM = 0.56; // RASTA's own reference antenna: f/D = 0.4

    }
}
