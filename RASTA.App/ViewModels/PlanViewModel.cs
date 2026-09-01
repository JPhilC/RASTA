using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Helpers;
using RASTA.App.Services;
using RASTA.Core.Antenna;
using RASTA.Core.Astro;
using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Telescope;
using RASTA.Core.Storage;
using RASTA.Processing.Planning;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RASTA.Infrastructure.Services;

namespace RASTA.App.ViewModels
{
    /// <summary>Whether the Plan view's sky map shows every capture point at once, or walks
    /// through them one at a time (see PlanViewModel.PlayAnimationCommand and friends).</summary>
    public enum PlanMapDisplayMode
    {
        All,
        Animate
    }

    /// <summary>Which reference geometry/labels the sky map draws - the dome's usual fixed
    /// altitude-ring/azimuth-spoke/compass-label frame, or an RA/Dec meridian/parallel grid
    /// projected fresh at MapTimeUtc (see EquatorialGridBuilder). A dome position is inherently
    /// Az/El, so this only changes which overlay is drawn, never the points' own positions.</summary>
    public enum PlanMapGridMode
    {
        AltAz,
        Equatorial
    }

    /// <summary>
    /// One capture point drawn on the Plan view's sky map. Sequence-coloured start-&gt;end via
    /// HeatmapImageBuilder.Ramp (order, not a measured value) unless AboveHorizon is false, in
    /// which case it's drawn dimmed/red regardless of sequence - see
    /// PlanViewModel.BuildOrderedPoints' horizon-limit fallback path.
    ///
    /// Fill is a pre-built, Frozen SolidColorBrush (not a bare Color left for the view to wrap in
    /// its own inline "SolidColorBrush Color={Binding}") - PlanView.xaml's point template binds
    /// Ellipse.Fill straight to it as a plain attribute. A Color bound through a nested
    /// SolidColorBrush element is itself a Freezable with its own binding, and once the point
    /// template picked up DataTemplate.Triggers (for the two-tone halo) that combination started
    /// throwing "Cannot find governing FrameworkElement" data-binding errors on every Points
    /// rebuild (e.g. every SelectedPlan/plan switch) - a known WPF trap for a bound Freezable
    /// sharing a template with named-target triggers. A Frozen brush has no inheritance-context
    /// to lose in the first place.
    ///
    /// DiameterPx is the dot's beamwidth-derived size in pixels (see PlanViewModel.ProjectPoints) -
    /// everything below is computed from it rather than stored, so an animated point's "current"
    /// enlarged size/margin stay correct through the `p with { IsCurrent = ... }` pattern
    /// PlanViewModel.RefreshVisiblePoints uses without needing its own constructor args.
    /// </summary>
    public record PlanMapPoint(
        int SequenceIndex,
        double X,
        double Y,
        double AzDeg,
        double ElDeg,
        Brush Fill,
        bool AboveHorizon,
        bool IsCurrent,
        string Tooltip,
        double DiameterPx)
    {
        public Thickness DotMargin => new Thickness(-DiameterPx / 2, -DiameterPx / 2, 0, 0);
        public double HaloDiameterPx => DiameterPx + 2;
        public Thickness HaloMargin => new Thickness(-HaloDiameterPx / 2, -HaloDiameterPx / 2, 0, 0);

        // Modest highlight for the actively-animated point, not a doubling - the old fixed
        // 9px->16px ratio (~1.78x) read as roughly twice the size once applied on top of a
        // beamwidth-derived base rather than a fixed one, so this is toned down to ~1.35x.
        public double CurrentDiameterPx => DiameterPx * 1.35;
        public Thickness CurrentDotMargin => new Thickness(-CurrentDiameterPx / 2, -CurrentDiameterPx / 2, 0, 0);
        public double CurrentHaloDiameterPx => CurrentDiameterPx + 4;
        public Thickness CurrentHaloMargin => new Thickness(-CurrentHaloDiameterPx / 2, -CurrentHaloDiameterPx / 2, 0, 0);
    }

    /// <summary>
    /// Rendered state for the Plan view's sky map - background, reference geometry, and
    /// whichever capture points are currently visible (see PlanViewModel.PointDisplayMode).
    /// Same "everything already in fixed pixel space" convention MosaicViewModel's dome/heatmap
    /// display records use, so the View can bind with simple one-to-one bindings.
    /// </summary>
    public class PlanMapDisplay
    {
        public double CanvasSize { get; init; }
        public double DomeLeft { get; init; }
        public double DomeTop { get; init; }
        public double DomeDiameter { get; init; }
        public BitmapSource? Background { get; init; }
        public IReadOnlyList<DomeRingGeometry> AltitudeRings { get; init; } = Array.Empty<DomeRingGeometry>();
        public IReadOnlyList<AxisGridLine> AzimuthSpokes { get; init; } = Array.Empty<AxisGridLine>();
        public IReadOnlyList<DomeCompassLabel> CompassLabels { get; init; } = Array.Empty<DomeCompassLabel>();
        public IReadOnlyList<PointCollection> EquatorialGridLines { get; init; } = Array.Empty<PointCollection>();
        public IReadOnlyList<PlanMapPoint> Points { get; init; } = Array.Empty<PlanMapPoint>();
        public PointCollection? RegionPolyline { get; init; }
        public string StatusText { get; init; } = string.Empty;
    }

    public partial class PlanViewModel : ObservableObject
    {
        private readonly SweepPlanner _planner;
        private readonly IPlanRepository _repository;
        private readonly UserOptionsService _userOptionsService;
        private readonly TelescopeState _telescopeState;
        private readonly StatusBarViewModel _statusBar;
        private readonly CaptureViewModel _captureViewModel;
        private readonly NavigationViewModel _navigationViewModel;
        private readonly IPlanEditorWindowService _planEditorWindowService;

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
                    LoadPlanCommand.NotifyCanExecuteChanged();
                    DeletePlanCommand.NotifyCanExecuteChanged();
                    CopyPlanCommand.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// Gates Load/Copy/Delete (see PlanView.xaml's saved-plans list) - all three stay visible
        /// always, just disabled via CanExecute rather than collapsed, the same convention the
        /// rest of the app uses (e.g. NavigationViewModel.CanNavigateCapture) rather than a
        /// Visibility binding.
        /// </summary>
        private bool CanEditOrDeleteSelectedPlan => SelectedPlan != null;

        // Currently edited plan
        [ObservableProperty]
        private PlanType planType = PlanType.Equatorial;

        partial void OnPlanTypeChanged(PlanType value)
        {
            OnPropertyChanged(nameof(CanDrawRegion));
            OnPropertyChanged(nameof(IsRangeGeometry));
            OnPropertyChanged(nameof(IsRegionGeometry));
            RefreshMapDisplay();
        }

        [ObservableProperty]
        private string friendlyName = "New Plan";

        // Sweep geometry
        [ObservableProperty] private TargetRange range = new();

        partial void OnRangeChanged(TargetRange? oldValue, TargetRange newValue)
        {
            if (oldValue != null)
                oldValue.PropertyChanged -= Range_PropertyChanged;
            newValue.PropertyChanged += Range_PropertyChanged;
            OnPropertyChanged(nameof(IsRangeGeometry));
            OnPropertyChanged(nameof(IsRegionGeometry));
            RefreshMapDisplay();
        }

        private void Range_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TargetRange.GeometryMode))
            {
                OnPropertyChanged(nameof(IsRangeGeometry));
                OnPropertyChanged(nameof(IsRegionGeometry));
            }
            RefreshMapDisplay();
        }

        /// <summary>
        /// Whether the Plan Editor's Equatorial RA/Dec Start/End boxes should show - Equatorial
        /// AND GeometryMode == Range. Bundles PlanType and Range.GeometryMode into one binding
        /// rather than needing a MultiBinding in PlanEditorWindow.xaml. AltAz's own Az/El
        /// Start/End boxes are unaffected by this - AltAz has no Region concept (see
        /// TargetRange.GeometryMode's own remarks) so they stay gated on PlanType alone.
        /// </summary>
        public bool IsRangeGeometry => PlanType == PlanType.Equatorial && Range.GeometryMode == SweepGeometryMode.Range;

        /// <summary>
        /// Whether the Plan Editor should show Region-mode content instead of Start/End boxes -
        /// Equatorial-only (see CanDrawRegion). The region itself is defined by drawing on the
        /// Plan view's map (StartDrawRegionCommand/FinishRegionCommand), not by typed fields, so
        /// this gates a short "drawn on the map" summary rather than input boxes.
        /// </summary>
        public bool IsRegionGeometry => PlanType == PlanType.Equatorial && Range.GeometryMode == SweepGeometryMode.Region;

        [ObservableProperty] private List<TargetPoint>? plannedPoints;

        // Capture parameters
        [ObservableProperty] private double dwellSeconds = 1;
        [ObservableProperty] private int filesPerPoint = 1;
        [ObservableProperty] private double sampleRate = 2_400_000;
        [ObservableProperty] private double centerFrequency = 1420_405_752.0;  // 1420.405752 MHz
        [ObservableProperty] private int fftBins = 4096;
        [ObservableProperty] private bool goToHomeAfterCapture = true;
        [ObservableProperty] private bool despikeEnabled = false;

        // Telescope parameters
        [ObservableProperty] private double settleTimeSeconds = 1;
        [ObservableProperty] private bool trackingEnabled = false;

        // Drift scan parameters
        [ObservableProperty] private double driftDeclinationDeg;
        [ObservableProperty] private double driftDurationMinutes = 10;
        [ObservableProperty] private double driftCadenceSeconds = 1;

        public PlanViewModel(
            SweepPlanner planner,
            SettingsViewModel settings,
            IPlanRepository repository,
            UserOptionsService userOptionsService,
            TelescopeState telescopeState,
            StatusBarViewModel statusBar,
            CaptureViewModel captureViewModel,
            NavigationViewModel navigationViewModel,
            IPlanEditorWindowService planEditorWindowService)
        {
            _planner = planner;
            _repository = repository;
            Settings = settings;
            _userOptionsService = userOptionsService;
            _telescopeState = telescopeState;
            _statusBar = statusBar;
            _captureViewModel = captureViewModel;
            _navigationViewModel = navigationViewModel;
            _planEditorWindowService = planEditorWindowService;

            LoadSavedPlans();

            range.PropertyChanged += Range_PropertyChanged;

            _statusBar.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(StatusBarViewModel.TelescopeConnected) ||
                    args.PropertyName == nameof(StatusBarViewModel.SdrConnected))
                {
                    UiThread.SafeInvoke(() => CaptureHereCommand.NotifyCanExecuteChanged());
                }
            };

            CenterFrequency = _userOptionsService.Options.DefaultCentreFrequencyHz;
            SampleRate = _userOptionsService.Options.DefaultBandwidthHz;
            FftBins = _userOptionsService.Options.DefaultFftSize;
            Range.AngularSeparationDeg = ComputeDefaultAngularSeparationDeg();

            SelectInitialPlan();

            RefreshMapDisplay();
        }

        /// <summary>
        /// Run once at startup - this view model is effectively a singleton for the app's lifetime
        /// (see CLAUDE.md "Project layering": one ServiceCollection built once, no scopes created
        /// afterward), so "when Plan is first opened" and "when this constructor runs" are the same
        /// moment. Opens the plan most recently used or modified - its saved file's own last-write
        /// time, touched by SavePlan (so this covers "edited and saved"; merely loading/selecting a
        /// plan doesn't itself write anything, so "used" and "modified" collapse to the same signal
        /// here) - falling back to the first plan in SavedPlans (ListPlans' own folder-scan order)
        /// if no file times differ, or to a brand new plan if none are saved at all. OrderByDescending
        /// is a stable sort, so when every plan's file time is equal (or all missing/unreadable,
        /// DateTime.MinValue) the fallback to "first in the list" falls out of the same call rather
        /// than needing a separate branch.
        /// </summary>
        private void SelectInitialPlan()
        {
            if (SavedPlans.Count == 0)
            {
                NewRangePlan();
                return;
            }

            var mostRecent = SavedPlans.OrderByDescending(GetPlanFileLastWriteTimeUtc).First();
            SelectedPlan = mostRecent;
            LoadPlan(mostRecent);
        }

        private DateTime GetPlanFileLastWriteTimeUtc(CapturePlan plan)
        {
            var path = Path.Combine(_userOptionsService.Options.PlansFolder, plan.FriendlyName + ".json");
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        /// <summary>
        /// Half the estimated antenna beamwidth (see AntennaUtils/SettingsViewModel.BeamwidthDeg)
        /// at this plan's own CenterFrequency - a sensible Nyquist-ish default point spacing for
        /// a brand new plan's sweep/region grid, used in place of the unhelpful 0 a fresh
        /// TargetRange starts with. Only applied where there's nothing to preserve (the initial
        /// state here, and NewRangePlanCommand/NewRegionPlanCommand) - LoadPlan/CopyPlan always
        /// keep whatever separation the saved plan already specifies.
        /// </summary>
        private double ComputeDefaultAngularSeparationDeg() =>
            AntennaUtils.ComputeBeamwidthDeg(Settings.DishDiameterM, CenterFrequency) * 0.5;

        private void LoadSavedPlans()
        {
            SavedPlans.Clear();
            foreach (var plan in _repository.ListPlans())
                SavedPlans.Add(plan);
        }


        // Build CapturePlan object
        public CapturePlan BuildCapturePlan()
        {
            return new CapturePlan
            {
                FriendlyName = FriendlyName,
                PlanType = PlanType,

                Range = Range,

                DwellTime = TimeSpan.FromSeconds(DwellSeconds),
                FilesPerPoint = FilesPerPoint,
                SampleRate = SampleRate,
                CenterFrequency = CenterFrequency,
                FftBins = FftBins,

                TrackingEnabled = TrackingEnabled,
                SettleTimeSeconds = SettleTimeSeconds,
                GoToHomeAfterCapture = GoToHomeAfterCapture,
                DespikeEnabled = DespikeEnabled,

                DriftDeclinationDeg = DriftDeclinationDeg,
                DriftDurationMinutes = DriftDurationMinutes,
                DriftCadenceSeconds = DriftCadenceSeconds
            };
        }

        // Save plan
        // Surfaced next to the Save Plan button on both PlanView's own toolbar and
        // PlanEditorWindow, so a save (successful or not) is never silent - previously nothing
        // told the user whether a plan (e.g. one just drawn as a region) actually got written to
        // disk, which read as "unable to save" even when it may have worked.
        [ObservableProperty]
        private string saveStatusText = string.Empty;

        [RelayCommand]
        private void SavePlan()
        {
            if (string.IsNullOrWhiteSpace(FriendlyName))
            {
                SaveStatusText = "Enter a plan name before saving.";
                MessageBox.Show(SaveStatusText, "Save Plan");
                return;
            }

            try
            {
                var plan = BuildCapturePlan();
                _repository.Save(plan);
                LoadSavedPlans();
                SaveStatusText = $"Saved '{FriendlyName}' at {DateTime.Now:HH:mm:ss}.";
            }
            catch (Exception ex)
            {
                SaveStatusText = $"Failed to save: {ex.Message}";
                MessageBox.Show(SaveStatusText, "Save Plan");
            }
        }

        // Load plan into editor
        [RelayCommand(CanExecute = nameof(CanEditOrDeleteSelectedPlan))]
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

            TrackingEnabled = plan.TrackingEnabled;
            SettleTimeSeconds = plan.SettleTimeSeconds;
            GoToHomeAfterCapture = plan.GoToHomeAfterCapture;
            DespikeEnabled = plan.DespikeEnabled;

            DriftDeclinationDeg = plan.DriftDeclinationDeg;
            DriftDurationMinutes = plan.DriftDurationMinutes;
            DriftCadenceSeconds = plan.DriftCadenceSeconds;

        }

        // New plan - Range geometry (the original "type numeric start/end boxes" definition,
        // and the only geometry AltAz plans support). Renamed from NewPlan/"New Plan" now that
        // NewRegionPlan/"New Region" exists alongside it - both create a plan with its intended
        // GeometryMode already set, rather than leaving it to be chosen (or left hazy) afterward.
        [RelayCommand]
        private void NewRangePlan()
        {
            FriendlyName = "New Plan";
            PlanType = PlanType.Equatorial;
            Range = new TargetRange { AngularSeparationDeg = ComputeDefaultAngularSeparationDeg() };
            PlannedPoints = null;
        }

        // New plan - Region geometry. Equatorial-only (see CanDrawRegion), so PlanType is forced
        // to Equatorial the same as NewRangePlan; GeometryMode is set to Region up front so the
        // editor's fields already reflect it (see IsRangeGeometry/IsRegionGeometry) even before
        // any vertices exist - RegionVertices stays empty until StartDrawRegionCommand/
        // FinishRegionCommand actually trace the loop on the map.
        [RelayCommand]
        private void NewRegionPlan()
        {
            FriendlyName = "New Plan";
            PlanType = PlanType.Equatorial;
            Range = new TargetRange
            {
                AngularSeparationDeg = ComputeDefaultAngularSeparationDeg(),
                GeometryMode = SweepGeometryMode.Region
            };
            PlannedPoints = null;
        }

        // Copy plan
        [RelayCommand(CanExecute = nameof(CanEditOrDeleteSelectedPlan))]
        private void CopyPlan(CapturePlan? plan)
        {
            if (plan == null)
                return;

            var copy = new CapturePlan
            {
                FriendlyName = plan.FriendlyName + " Copy",
                PlanType = plan.PlanType,
                Range = plan.Range.Clone(),   // You may want to implement Clone()

                DwellTime = plan.DwellTime,
                FilesPerPoint = plan.FilesPerPoint,
                SampleRate = plan.SampleRate,
                CenterFrequency = plan.CenterFrequency,
                FftBins = plan.FftBins,

                TrackingEnabled = plan.TrackingEnabled,
                SettleTimeSeconds = plan.SettleTimeSeconds,
                GoToHomeAfterCapture = plan.GoToHomeAfterCapture,
                DespikeEnabled = plan.DespikeEnabled,

                DriftDeclinationDeg = plan.DriftDeclinationDeg,
                DriftDurationMinutes = plan.DriftDurationMinutes,
                DriftCadenceSeconds = plan.DriftCadenceSeconds
            };

            LoadPlan(copy);
        }

        // Delete plan
        [RelayCommand(CanExecute = nameof(CanEditOrDeleteSelectedPlan))]
        private void DeletePlan(CapturePlan? plan)
        {
            if (plan == null)
                return;

            var fileName = plan.FriendlyName + ".json";
            var path = Path.Combine(_userOptionsService.Options.PlansFolder, fileName);

            if (File.Exists(path))
                File.Delete(path);

            // Re-select a neighbour rather than leaving the list with nothing selected: the plan
            // that follows the deleted one (which, after reload, has shifted down into the deleted
            // one's old index), or the previous one if the deleted plan was last in the list.
            int deletedIndex = SavedPlans.IndexOf(plan);
            LoadSavedPlans();

            SelectedPlan = SavedPlans.Count == 0
                ? null
                : SavedPlans[Math.Clamp(deletedIndex, 0, SavedPlans.Count - 1)];
        }

        // =====================================================================================
        // Sky map - see CLAUDE.md's "Radio Sky map on the Plan view" plan for the overall design.
        // =====================================================================================

        private const double MapCanvasSize = 640;
        private const double MapMarginPx = 50;
        // 512, not the on-screen canvas's own 640 - the Milky Way background is now real HI4PI
        // survey structure (see Hi4PiSkyMap) rather than a smooth analytic Gaussian, so it holds
        // up much better under zoom/inspection at higher internal resolution than the original
        // 240 chosen for the old approximation; still deliberately short of 640 so there's some
        // margin left for the Image control's own bilinear upscale to smooth over, rather than
        // rendering pixel-for-pixel and then downscaling for nothing.
        private const int BackgroundPixelSize = 512;

        private readonly DomeProjector _projector = new(MapCanvasSize, MapMarginPx);

        private IReadOnlyList<DomeRingGeometry> _cachedAltAzRings = Array.Empty<DomeRingGeometry>();
        private IReadOnlyList<AxisGridLine> _cachedAltAzSpokes = Array.Empty<AxisGridLine>();
        private IReadOnlyList<DomeCompassLabel> _cachedAltAzLabels = Array.Empty<DomeCompassLabel>();

        private IReadOnlyList<PointCollection> _cachedEquatorialGridLines = Array.Empty<PointCollection>();
        private IReadOnlyList<DomeCompassLabel> _cachedEquatorialLabels = Array.Empty<DomeCompassLabel>();

        private BitmapSource? _cachedBackground;
        private DateTime _cachedBackgroundTimeKey;
        private double _cachedBackgroundLat = double.NaN;
        private double _cachedBackgroundLon = double.NaN;

        private List<PlanMapPoint> _cachedPoints = new();
        private string _lastStatusText = string.Empty;
        private PointCollection? _drawingPolyline;

        private DateTime mapTimeUtc = DateTime.UtcNow;

        /// <summary>
        /// The map's displayed/prospective-start time. Hand-written (not [ObservableProperty])
        /// so every set can be normalized to DateTimeKind.Utc first (relabelled, not shifted -
        /// "treat whatever was typed as UTC directly"), regardless of what Kind the source
        /// produced. WPF's default DateTimeConverter parses plain typed text (no timezone
        /// designator) as DateTimeKind.Unspecified, and AstronomyUtils' own helpers treat an
        /// Unspecified DateTime as LOCAL system time, silently shifting it via ToUniversalTime()
        /// by the machine's UTC offset - EquatorialGridBuilder/MilkyWayBackgroundBuilder already
        /// defend against this themselves, but ProjectPoints/HandleMap*/UpdateDrawingPreview read
        /// this property directly, so normalizing once here (rather than at every call site) is
        /// what actually guarantees every reader agrees on what moment MapTimeUtc means.
        /// </summary>
        public DateTime MapTimeUtc
        {
            get => mapTimeUtc;
            set
            {
                var normalized = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
                if (SetProperty(ref mapTimeUtc, normalized))
                    RefreshMapDisplay();
            }
        }

        [RelayCommand]
        private void SetMapTimeNow() => MapTimeUtc = DateTime.UtcNow;

        [ObservableProperty]
        private PlanMapGridMode mapGridMode = PlanMapGridMode.AltAz;

        partial void OnMapGridModeChanged(PlanMapGridMode value) => RefreshMapDisplay();

        [ObservableProperty]
        private PlanMapDisplayMode pointDisplayMode = PlanMapDisplayMode.All;

        partial void OnPointDisplayModeChanged(PlanMapDisplayMode value)
        {
            if (value == PlanMapDisplayMode.Animate)
                AnimationCurrentIndex = -1;
            RefreshVisiblePoints();
        }

        [ObservableProperty]
        private int animationCurrentIndex = -1;

        partial void OnAnimationCurrentIndexChanged(int value) => RefreshVisiblePoints();

        [ObservableProperty]
        private bool isAnimationPlaying;

        [ObservableProperty]
        private double animationSpeedSecondsPerPoint = 0.5;

        private DispatcherTimer? _animationTimer;

        [ObservableProperty]
        private PlanMapDisplay mapDisplay = new();

        [ObservableProperty]
        private string hoverReadoutText = string.Empty;

        [ObservableProperty]
        private bool isDrawingRegion;

        partial void OnIsDrawingRegionChanged(bool value)
        {
            OnPropertyChanged(nameof(CanClearRegion));
            ClearRegionCommand.NotifyCanExecuteChanged();
        }

        private readonly List<RegionVertex> _drawingVertices = new();

        public bool CanDrawRegion => PlanType == PlanType.Equatorial;

        /// <summary>
        /// Whether there's anything for ClearRegionCommand to actually clear - either an
        /// in-progress drawing, or an already-finished region sitting on Range.RegionVertices.
        /// Without the latter half, the Clear button would vanish the moment Finish Region ran
        /// (IsDrawingRegion goes false), leaving no way to get rid of an already-committed region
        /// short of drawing a new one over it.
        /// </summary>
        public bool CanClearRegion => IsDrawingRegion || Range.RegionVertices.Count > 0;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CaptureHereCommand))]
        private TargetPoint? contextTargetPoint;

        public bool CanCaptureHere => ContextTargetPoint != null && _statusBar.TelescopeConnected && _statusBar.SdrConnected;

        /// <summary>
        /// Recomputes the map's geometry-dependent state (background, reference rings/spokes/
        /// labels, and the plan's own ordered/validated - or, on failure, raw - capture points).
        /// Wired up from every property that can change what the map should show: PlanType,
        /// Range and everything inside it (RA/Dec-or-Az/El Start/End, AngularSeparationDeg,
        /// GeometryMode, RegionVertices - see Range_PropertyChanged), and MapTimeUtc.
        /// </summary>
        private void RefreshMapDisplay()
        {
            RefreshBackgroundIfNeeded();

            if (_cachedAltAzRings.Count == 0)
            {
                _cachedAltAzRings = _projector.BuildAltitudeRings();
                _cachedAltAzSpokes = _projector.BuildAzimuthSpokes();
                _cachedAltAzLabels = _projector.BuildCompassLabels();
            }

            if (MapGridMode == PlanMapGridMode.Equatorial)
                RefreshEquatorialGrid();

            _cachedPoints = BuildOrderedPoints(out _lastStatusText);

            if (AnimationCurrentIndex >= _cachedPoints.Count)
                AnimationCurrentIndex = _cachedPoints.Count - 1;

            OnPropertyChanged(nameof(CanClearRegion));
            ClearRegionCommand.NotifyCanExecuteChanged();
            RefreshVisiblePoints();
        }

        private static DateTime RoundToMinute(DateTime utc) =>
            new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

        private void RefreshBackgroundIfNeeded()
        {
            var timeKey = RoundToMinute(MapTimeUtc);
            if (_cachedBackground != null &&
                _cachedBackgroundTimeKey == timeKey &&
                Math.Abs(_cachedBackgroundLat - Settings.SiteLatitudeDeg) < 1e-6 &&
                Math.Abs(_cachedBackgroundLon - Settings.SiteLongitudeDeg) < 1e-6)
            {
                return;
            }

            _cachedBackground = MilkyWayBackgroundBuilder.Build(
                BackgroundPixelSize, MapMarginPx / MapCanvasSize, MapTimeUtc,
                Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);
            _cachedBackgroundTimeKey = timeKey;
            _cachedBackgroundLat = Settings.SiteLatitudeDeg;
            _cachedBackgroundLon = Settings.SiteLongitudeDeg;
        }

        /// <summary>
        /// Rebuilds the RA/Dec grid unconditionally - deliberately no "has anything actually
        /// changed" cache guard (unlike RefreshBackgroundIfNeeded), since this is the one piece
        /// of MapTimeUtc-driven state on the map and it's cheap enough (a couple of thousand
        /// trig calls) that always recomputing it removes any chance of it going stale.
        /// </summary>
        private void RefreshEquatorialGrid()
        {
            var (lines, labels) = EquatorialGridBuilder.Build(
                _projector, MapTimeUtc, Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);
            _cachedEquatorialGridLines = lines;
            _cachedEquatorialLabels = labels;
        }

        /// <summary>
        /// Runs the plan through the real SweepPlanner ordering/horizon-validation pipeline
        /// (treating MapTimeUtc as the plan's prospective start time) so the map shows exactly
        /// what an actual sweep would do - true execution order and ETA. On failure (e.g. a
        /// point below the horizon limit), falls back to the raw, unordered/unvalidated grid so
        /// a failing plan is still diagnosable on the map rather than just an error message.
        /// </summary>
        private List<PlanMapPoint> BuildOrderedPoints(out string statusText)
        {
            if (PlanType == PlanType.Drift)
            {
                statusText = "Drift plans have no sweep points to preview.";
                return new List<PlanMapPoint>();
            }

            var plan = BuildCapturePlan();
            var result = _planner.BuildSweep(
                plan,
                MapTimeUtc,
                TimeSpan.FromSeconds(DwellSeconds),
                SettleTimeSeconds,
                Settings.SiteLatitudeDeg,
                Settings.SiteLongitudeDeg,
                Settings.HorizonLimitDeg,
                Settings.SlewRateDegPerSec);

            if (result.Success)
            {
                statusText = $"{result.Points.Count} point(s). Estimated completion: {result.EstimatedCompletionUtc:yyyy-MM-dd HH:mm} UTC (from {MapTimeUtc:yyyy-MM-dd HH:mm} UTC start).";
                if (result.Warning != null)
                    statusText += $" {result.Warning}";
                return ProjectPoints(result.Points, allAboveHorizon: true);
            }

            var (rawPoints, _) = _planner.BuildRawPoints(plan);
            statusText = rawPoints.Count == 0
                ? result.ErrorMessage ?? "Plan has no points to preview."
                : $"{rawPoints.Count} point(s) shown unordered - {result.ErrorMessage}";
            return ProjectPoints(rawPoints, allAboveHorizon: false);
        }

        // Floor so a tight beamwidth (high frequency / large dish) never collapses to a
        // sub-pixel, effectively invisible dot - see [[plan-sky-map-beamwidth-dots]].
        private const double MinDotDiameterPx = 6.0;

        private List<PlanMapPoint> ProjectPoints(IReadOnlyList<TargetPoint> points, bool allAboveHorizon)
        {
            var result = new List<PlanMapPoint>(points.Count);
            int n = points.Count;

            // Dot size represents the antenna's actual sky footprint: beamwidth in degrees
            // converted to pixels via the dome projector's constant radial scale
            // (r = (90-el)/90 * Radius, so pixels-per-degree = Radius/90 everywhere - see
            // DomeProjector). Same beamwidth estimate ComputeDefaultAngularSeparationDeg already
            // uses, evaluated at this plan's own CenterFrequency.
            double beamwidthDeg = AntennaUtils.ComputeBeamwidthDeg(Settings.DishDiameterM, CenterFrequency);
            double pxPerDeg = _projector.Radius / 90.0;
            double dotDiameterPx = Math.Max(beamwidthDeg * pxPerDeg, MinDotDiameterPx);

            for (int i = 0; i < n; i++)
            {
                var p = points[i];
                double azDeg, elDeg;
                string coordText;

                if (p.Mode == CoordinateMode.AltAz)
                {
                    azDeg = p.AzimuthDeg;
                    elDeg = p.ElevationDeg;
                    coordText = $"Az {azDeg:F1}°  El {elDeg:F1}°";
                }
                else
                {
                    (azDeg, elDeg) = AstronomyUtils.EquatorialToHorizontal(
                        p.RightAscensionHours, p.DeclinationDeg, MapTimeUtc,
                        Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);
                    coordText = $"RA {p.RightAscensionHours:F2}h  Dec {p.DeclinationDeg:F2}°";
                }

                bool aboveHorizon = allAboveHorizon || elDeg >= Settings.HorizonLimitDeg;
                var (x, y) = _projector.Project(azDeg, elDeg);

                Color color;
                if (aboveHorizon)
                {
                    double t = n > 1 ? (double)i / (n - 1) : 0.0;
                    var (r, g, b) = HeatmapImageBuilder.Ramp(t);
                    color = Color.FromRgb(r, g, b);
                }
                else
                {
                    color = Color.FromRgb(0x99, 0x33, 0x33); // dimmed red - below the horizon limit at MapTimeUtc
                }
                var fill = new SolidColorBrush(color);
                fill.Freeze();

                string tooltip = $"#{i + 1}\n{coordText}\nAz {azDeg:F1}°  El {elDeg:F1}°" +
                    (aboveHorizon ? string.Empty : "\n(below horizon limit)");

                result.Add(new PlanMapPoint(i, x, y, azDeg, elDeg, fill, aboveHorizon, false, tooltip, dotDiameterPx));
            }

            return result;
        }

        /// <summary>
        /// Rebuilds MapDisplay.Points from the already-computed _cachedPoints, honouring
        /// PointDisplayMode/AnimationCurrentIndex - cheap, so this is what mode/animation-step
        /// changes call directly instead of the full RefreshMapDisplay (which recomputes
        /// background/geometry/ordering).
        /// </summary>
        private void RefreshVisiblePoints()
        {
            List<PlanMapPoint> visible;

            if (PointDisplayMode == PlanMapDisplayMode.Animate)
            {
                int count = Math.Max(AnimationCurrentIndex + 1, 0);
                // The final point never gets the enlarged "current" treatment: reaching it is
                // always the end of the run (Play pauses itself the tick after arriving there,
                // Step simply can't advance further), so without this it was left stuck enlarged
                // indefinitely once the animation finished instead of matching the other points.
                bool isFinalPoint = AnimationCurrentIndex >= _cachedPoints.Count - 1;
                visible = _cachedPoints
                    .Take(count)
                    .Select((p, i) => p with { IsCurrent = i == AnimationCurrentIndex && !isFinalPoint })
                    .ToList();
            }
            else
            {
                visible = _cachedPoints;
            }

            bool altAz = MapGridMode == PlanMapGridMode.AltAz;

            MapDisplay = new PlanMapDisplay
            {
                CanvasSize = MapCanvasSize,
                DomeLeft = _projector.CenterX - _projector.Radius,
                DomeTop = _projector.CenterY - _projector.Radius,
                DomeDiameter = _projector.Radius * 2,
                Background = _cachedBackground,
                AltitudeRings = altAz ? _cachedAltAzRings : Array.Empty<DomeRingGeometry>(),
                AzimuthSpokes = altAz ? _cachedAltAzSpokes : Array.Empty<AxisGridLine>(),
                CompassLabels = altAz ? _cachedAltAzLabels : _cachedEquatorialLabels,
                EquatorialGridLines = altAz ? Array.Empty<PointCollection>() : _cachedEquatorialGridLines,
                Points = visible,
                RegionPolyline = _drawingPolyline,
                StatusText = _lastStatusText
            };
        }

        // ---- Animation ----

        [RelayCommand]
        private void PlayAnimation()
        {
            if (_cachedPoints.Count == 0)
                return;

            PointDisplayMode = PlanMapDisplayMode.Animate;
            if (AnimationCurrentIndex >= _cachedPoints.Count - 1)
                AnimationCurrentIndex = -1;

            _animationTimer ??= new DispatcherTimer();
            _animationTimer.Interval = TimeSpan.FromSeconds(Math.Max(AnimationSpeedSecondsPerPoint, 0.05));
            _animationTimer.Tick -= AnimationTimer_Tick;
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();
            IsAnimationPlaying = true;
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            if (AnimationCurrentIndex >= _cachedPoints.Count - 1)
            {
                PauseAnimation();
                return;
            }
            AnimationCurrentIndex++;
        }

        [RelayCommand]
        private void PauseAnimation()
        {
            _animationTimer?.Stop();
            IsAnimationPlaying = false;
        }

        [RelayCommand]
        private void StepAnimation()
        {
            if (_cachedPoints.Count == 0)
                return;

            PauseAnimation();
            PointDisplayMode = PlanMapDisplayMode.Animate;
            if (AnimationCurrentIndex < _cachedPoints.Count - 1)
                AnimationCurrentIndex++;
        }

        [RelayCommand]
        private void ResetAnimation()
        {
            PauseAnimation();
            AnimationCurrentIndex = -1;
        }

        // ---- Region drawing ----

        [RelayCommand(CanExecute = nameof(CanDrawRegion))]
        private void StartDrawRegion()
        {
            _drawingVertices.Clear();
            IsDrawingRegion = true;
            UpdateDrawingPreview();
        }

        [RelayCommand]
        private void FinishRegion()
        {
            if (_drawingVertices.Count < 3)
            {
                MessageBox.Show("Draw at least 3 points to define a region.", "Draw Region");
                return;
            }

            Range.RegionVertices = _drawingVertices.Select(v => new RegionVertex(v.RaHours, v.DecDeg)).ToList();
            Range.GeometryMode = SweepGeometryMode.Region;

            IsDrawingRegion = false;
            _drawingVertices.Clear();
            UpdateDrawingPreview();
            RefreshMapDisplay();
        }

        // CanExecute'd on CanClearRegion so the button stays usable both mid-drawing (cancels
        // it) and after Finish Region has already committed a region (clears the committed one
        // too, resetting GeometryMode back to Range) - previously this only ever cleared the
        // in-progress drawing, so a finished region could never actually be removed.
        [RelayCommand(CanExecute = nameof(CanClearRegion))]
        private void ClearRegion()
        {
            IsDrawingRegion = false;
            _drawingVertices.Clear();

            if (Range.RegionVertices.Count > 0)
            {
                Range.RegionVertices = new List<RegionVertex>();
                if (Range.GeometryMode == SweepGeometryMode.Region)
                    Range.GeometryMode = SweepGeometryMode.Range;
            }

            UpdateDrawingPreview();
            RefreshMapDisplay();
        }

        private void UpdateDrawingPreview()
        {
            if (_drawingVertices.Count == 0)
            {
                _drawingPolyline = null;
                RefreshVisiblePoints();
                return;
            }

            var pts = new PointCollection();
            foreach (var v in _drawingVertices)
            {
                var (azDeg, elDeg) = AstronomyUtils.EquatorialToHorizontal(
                    v.RaHours, v.DecDeg, MapTimeUtc, Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);
                var (x, y) = _projector.Project(azDeg, elDeg);
                pts.Add(new Point(x, y));
            }
            if (pts.Count > 2)
                pts.Add(pts[0]); // close the loop visually while drawing

            pts.Freeze();
            _drawingPolyline = pts;
            RefreshVisiblePoints();
        }

        // ---- Mouse interaction (called from PlanView's code-behind) ----

        /// <summary>Hover readout (Az/El + RA/Dec under the cursor); also extends the in-progress
        /// region polyline preview while drawing.</summary>
        public void HandleMapMouseMove(double x, double y)
        {
            var azel = _projector.Unproject(x, y);
            if (azel is null)
            {
                HoverReadoutText = string.Empty;
                return;
            }

            var (azDeg, elDeg) = azel.Value;
            var (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(
                azDeg, elDeg, MapTimeUtc, Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);

            HoverReadoutText = $"Az {azDeg:F1}°  El {elDeg:F1}°    RA {raHours:F2}h  Dec {decDeg:F2}°";
        }

        /// <summary>Left-click: only meaningful while drawing a region, adds a vertex.</summary>
        public void HandleMapLeftClick(double x, double y)
        {
            if (!IsDrawingRegion)
                return;

            var azel = _projector.Unproject(x, y);
            if (azel is null)
                return; // below the horizon - not a usable region vertex

            var (azDeg, elDeg) = azel.Value;
            var (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(
                azDeg, elDeg, MapTimeUtc, Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);

            _drawingVertices.Add(new RegionVertex(raHours, decDeg));
            UpdateDrawingPreview();
        }

        /// <summary>
        /// Right-click: computes the sky point under the cursor (in whatever coordinate mode the
        /// connected mount is actually in, since ContextTargetPoint feeds a real slew via
        /// CaptureHereCommand) and stashes it into ContextTargetPoint before the View's
        /// ContextMenu opens, so "Slew &amp; Capture Here"'s CanExecute already reflects it.
        /// </summary>
        public void HandleMapRightClick(double x, double y)
        {
            var azel = _projector.Unproject(x, y);
            if (azel is null)
            {
                ContextTargetPoint = null;
                return;
            }

            var (azDeg, elDeg) = azel.Value;

            if (_telescopeState.Mode == CoordinateMode.Equatorial)
            {
                var (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(
                    azDeg, elDeg, MapTimeUtc, Settings.SiteLatitudeDeg, Settings.SiteLongitudeDeg);
                ContextTargetPoint = TargetPoint.FromRaDec(raHours, decDeg);
            }
            else
            {
                ContextTargetPoint = TargetPoint.FromAzEl(azDeg, elDeg);
            }
        }

        [RelayCommand(CanExecute = nameof(CanCaptureHere))]
        private void CaptureHere()
        {
            if (ContextTargetPoint is null)
                return;

            _captureViewModel.PendingQuickCaptureTarget = ContextTargetPoint;

            if (_navigationViewModel.NavigateCaptureCommand.CanExecute(null))
                _navigationViewModel.NavigateCaptureCommand.Execute(null);
        }

        [RelayCommand]
        private void OpenPlanEditor() => _planEditorWindowService.ShowOrActivate(this);
    }
}
