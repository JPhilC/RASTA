using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.ViewModels;
using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;
using RASTA.Core.Storage;
using RASTA.Processing.Planning;
using System.Collections.ObjectModel;
using System.IO;
using RASTA.Infrastructure.Services;

namespace RASTA.App.ViewModels
{


    public partial class PlanViewModel : ObservableObject
    {
        private readonly SdrState _sdrState;
        private readonly SweepPlanner _planner;
        private readonly IPlanRepository _repository;
        private readonly UserOptionsService _userOptionsService;

        public SettingsViewModel Settings { get; }

        // List of saved plans
        public ObservableCollection<CapturePlan> SavedPlans { get; } = new();

        
        private CapturePlan? selectedPlan;

        public CapturePlan? SelectedPlan
        {
            get => selectedPlan;
            set
            {
                if (SetProperty(ref selectedPlan, value))
                {
                    System.Diagnostics.Debug.WriteLine($"SelectedPlan changed to: {selectedPlan?.FriendlyName}");
                }
            }
        }

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
        [ObservableProperty] private int filesPerPoint = 1;
        [ObservableProperty] private double sampleRate = 2_400_000;
        [ObservableProperty] private double centerFrequency = 1420_405_751;
        [ObservableProperty] private int fftBins = 4096;
        [ObservableProperty] private int integrations = 1;
        [ObservableProperty] private bool goToHomeAfterCapture = true;

        // Telescope parameters
        [ObservableProperty] private double settleTimeSeconds = 1;
        [ObservableProperty] private bool trackingEnabled = false;

        // Drift scan parameters
        [ObservableProperty] private double driftDeclinationDeg;
        [ObservableProperty] private double driftDurationMinutes = 10;
        [ObservableProperty] private double driftCadenceSeconds = 1;

        public PlanViewModel(SdrState sdrState, 
            SweepPlanner planner, 
            SettingsViewModel settings, 
            IPlanRepository repository,
            UserOptionsService userOptionsService)
        {
            _sdrState = sdrState;
            _planner = planner;
            _repository = repository;
            Settings = settings;
            _userOptionsService = userOptionsService;

            LoadSavedPlans();

            _sdrState.PropertyChanged += SdrState_PropertyChanged;
        }

        private void SdrState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SdrState.SelectedDevice))
            {
                LoadSavedPlans();
            }
        }

        private void LoadSavedPlans()
        {
            SavedPlans.Clear();
            var sdrDeviceId = _sdrState.SelectedDevice?.DeviceId ?? "UNKNOWN";
            foreach (var plan in _repository.ListPlans(sdrDeviceId))
                SavedPlans.Add(plan);
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
                FilesPerPoint = FilesPerPoint,
                SampleRate = SampleRate,
                CenterFrequency = CenterFrequency,
                FftBins = FftBins,
                Integrations = Integrations,

                TrackingEnabled = TrackingEnabled,
                SettleTimeSeconds = SettleTimeSeconds,
                GoToHomeAfterCapture = GoToHomeAfterCapture,

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
            FilesPerPoint = plan.FilesPerPoint;   
            SampleRate = plan.SampleRate;
            CenterFrequency = plan.CenterFrequency;
            FftBins = plan.FftBins;
            Integrations = plan.Integrations;

            TrackingEnabled = plan.TrackingEnabled;
            SettleTimeSeconds = plan.SettleTimeSeconds;
            GoToHomeAfterCapture = plan.GoToHomeAfterCapture;

            DriftDeclinationDeg = plan.DriftDeclinationDeg;
            DriftDurationMinutes = plan.DriftDurationMinutes;
            DriftCadenceSeconds = plan.DriftCadenceSeconds;

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
                FilesPerPoint = plan.FilesPerPoint,
                SampleRate = plan.SampleRate,
                CenterFrequency = plan.CenterFrequency,
                FftBins = plan.FftBins,
                Integrations = plan.Integrations,

                TrackingEnabled = plan.TrackingEnabled,
                SettleTimeSeconds = plan.SettleTimeSeconds,
                GoToHomeAfterCapture = plan.GoToHomeAfterCapture,

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
            var path = Path.Combine(_userOptionsService.Options.PlansFolder, fileName);

            if (File.Exists(path))
                File.Delete(path);

            LoadSavedPlans();
        }
    }
}

