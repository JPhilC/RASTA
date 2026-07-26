using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.ViewModels;
using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Storage;
using RASTA.Processing.Planning;
using System.Collections.ObjectModel;
using System.IO;

namespace RASTA.App.ViewModels
{


    public partial class PlanViewModel : ObservableObject
    {
        private readonly SdrState _sdrState;
        private readonly SweepPlanner _planner;
        private readonly IPlanRepository _repository;

        public SettingsViewModel Settings { get; }

        // List of saved plans
        public ObservableCollection<CapturePlan> SavedPlans { get; } = new();

        [ObservableProperty]
        private CapturePlan? selectedSavedPlan;

        // Currently edited plan
        [ObservableProperty]
        private PlanType planType = PlanType.Equatorial;

        [ObservableProperty]
        private string friendlyName = "New Plan";

        // Sweep geometry
        [ObservableProperty] private TargetRange range = new();

        [ObservableProperty] private List<TargetPoint>? plannedPoints;

        // Capture parameters
        [ObservableProperty] private double dwellSeconds = 1;
        [ObservableProperty] private double sampleRate = 2_400_000;
        [ObservableProperty] private double centerFrequency = 1420_405_751;
        [ObservableProperty] private int fftBins = 4096;
        [ObservableProperty] private int integrations = 1;
        [ObservableProperty] private double gain = 0;

        // Telescope parameters
        [ObservableProperty] private double settleTimeSeconds = 1;
        [ObservableProperty] private bool trackingEnabled = false;

        // Output
        [ObservableProperty] private string outputFolder = "Captures";
        [ObservableProperty] private string filePrefix = "rasta_";

        // Drift scan parameters
        [ObservableProperty] private double driftDeclinationDeg;
        [ObservableProperty] private double driftDurationMinutes = 10;
        [ObservableProperty] private double driftCadenceSeconds = 1;

        public PlanViewModel(SdrState sdrState, SweepPlanner planner, SettingsViewModel settings, IPlanRepository repository)
        {
            _sdrState = sdrState;
            _planner = planner;
            _repository = repository;
            Settings = settings;

            LoadSavedPlans();
        }

        private void LoadSavedPlans()
        {
            SavedPlans.Clear();
            var sdrDeviceId = _sdrState.SelectedDevice?.DeviceId ?? "UNKNOWN";
            foreach (var plan in _repository.ListPlans(sdrDeviceId))
                SavedPlans.Add(plan);
        }

        // Build sweep points (Equatorial or AltAz only)
        [RelayCommand]
        private void BuildSweep()
        {
            if (PlanType == PlanType.Drift)
            {
                PlannedPoints = new List<TargetPoint>(); // Drift has no points
                return;
            }

            Range.Mode = PlanType == PlanType.Equatorial
                ? CoordinateMode.Equatorial
                : CoordinateMode.AltAz;

            Range.DwellTime = TimeSpan.FromSeconds(DwellSeconds);

            PlannedPoints = _planner.BuildSweep(Range).ToList();
        }

        // Build CapturePlan object
        public CapturePlan BuildCapturePlan()
        {
            return new CapturePlan
            {
                SdrDeviceId = (_sdrState.SelectedDevice?.DeviceId ?? "UNKNOWN"),

                FriendlyName = FriendlyName,
                PlanType = PlanType,

                Range = Range,

                DwellTime = TimeSpan.FromSeconds(DwellSeconds),
                SampleRate = SampleRate,
                CenterFrequency = CenterFrequency,
                FftBins = FftBins,
                Integrations = Integrations,
                Gain = Gain,

                TrackingEnabled = TrackingEnabled,
                SettleTimeSeconds = SettleTimeSeconds,

                OutputFolder = OutputFolder,
                FilePrefix = FilePrefix,

                DriftDeclinationDeg = DriftDeclinationDeg,
                DriftDurationMinutes = DriftDurationMinutes,
                DriftCadenceSeconds = DriftCadenceSeconds
            };
        }

        // Save plan
        [RelayCommand]
        private void SavePlan()
        {
            var plan = BuildCapturePlan();
            _repository.Save(plan);
            LoadSavedPlans();
        }

        // Load plan into editor
        [RelayCommand]
        private void LoadPlan(CapturePlan? plan)
        {
            if (plan == null)
                return;

            FriendlyName = plan.FriendlyName;
            PlanType = plan.PlanType;

            Range = plan.Range;   // Restore sweep inputs

            DwellSeconds = plan.DwellTime.TotalSeconds;
            SampleRate = plan.SampleRate;
            CenterFrequency = plan.CenterFrequency;
            FftBins = plan.FftBins;
            Integrations = plan.Integrations;
            Gain = plan.Gain;

            TrackingEnabled = plan.TrackingEnabled;
            SettleTimeSeconds = plan.SettleTimeSeconds;

            OutputFolder = plan.OutputFolder;
            FilePrefix = plan.FilePrefix;

            DriftDeclinationDeg = plan.DriftDeclinationDeg;
            DriftDurationMinutes = plan.DriftDurationMinutes;
            DriftCadenceSeconds = plan.DriftCadenceSeconds;

            // Rebuild sweep points from Range
            BuildSweep();
        }

        // New plan
        [RelayCommand]
        private void NewPlan()
        {
            FriendlyName = "New Plan";
            PlanType = PlanType.Equatorial;
            Range = new TargetRange();
            PlannedPoints = null;
        }

        // Copy plan
        [RelayCommand]
        private void CopyPlan(CapturePlan? plan)
        {
            if (plan == null)
                return;

            var copy = new CapturePlan
            {
                SdrDeviceId = plan.SdrDeviceId,
                FriendlyName = plan.FriendlyName + " Copy",
                PlanType = plan.PlanType,
                Range = plan.Range.Clone(),   // You may want to implement Clone()

                DwellTime = plan.DwellTime,
                SampleRate = plan.SampleRate,
                CenterFrequency = plan.CenterFrequency,
                FftBins = plan.FftBins,
                Integrations = plan.Integrations,
                Gain = plan.Gain,

                TrackingEnabled = plan.TrackingEnabled,
                SettleTimeSeconds = plan.SettleTimeSeconds,

                OutputFolder = plan.OutputFolder,
                FilePrefix = plan.FilePrefix,

                DriftDeclinationDeg = plan.DriftDeclinationDeg,
                DriftDurationMinutes = plan.DriftDurationMinutes,
                DriftCadenceSeconds = plan.DriftCadenceSeconds
            };

            LoadPlan(copy);
        }

        // Delete plan
        [RelayCommand]
        private void DeletePlan(CapturePlan plan)
        {
            var fileName = plan.FriendlyName + ".json";
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RASTA", "Plans", fileName);

            if (File.Exists(path))
                File.Delete(path);

            LoadSavedPlans();
        }
    }
}

