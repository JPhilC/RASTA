using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Helpers;
using RASTA.Core.Astro;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Processing.Dsp;
using RASTA.Processing.Gridding;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.Mosaic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RASTA.App.ViewModels;

/// <summary>
/// One row for the Positions summary DataGrid - a read-only projection of MosaicPosition,
/// not the full HiSpectrum (which the DataGrid has no use for).
/// </summary>
public record MosaicPositionSummary(
    string Label, string Coordinates, double LineStrengthDb, double PeakVelocityKmPerSec, int FileCount);

/// <summary>
/// Which MosaicPosition metric the "3D Surface" tab renders as height/colour - see
/// MosaicViewModel.RenderSurface. The "Sky Mosaic" 2D heatmap always shows LineStrengthDb
/// regardless of this toggle (a position-velocity map only means "toward/away from LSR", which
/// doesn't read as a colour-brightness map the way dB does).
/// </summary>
public enum MosaicSurfaceMetric
{
    Strength,
    Velocity
}

/// <summary>
/// One rendered heatmap panel's worth of display state - always replaced as a whole new
/// instance on rebuild (rather than mutated in place) so a single property-changed
/// notification on the containing MosaicViewModel property refreshes every nested binding.
/// PixelWidth/PixelHeight and the tick/gridline collections are all in that same fixed pixel
/// space - see RenderSkyHeatmap and MosaicView.xaml's axis overlay, which sizes the Image and
/// its overlaid ItemsControls identically so a tick's Position lines up exactly with the data.
/// </summary>
public class MosaicHeatmapDisplay
{
    public BitmapSource? Image { get; init; }
    public double PixelWidth { get; init; }
    public double PixelHeight { get; init; }
    public string XAxisLabel { get; init; } = string.Empty;
    public string YAxisLabel { get; init; } = string.Empty;
    public string LegendMinText { get; init; } = string.Empty;
    public string LegendMaxText { get; init; } = string.Empty;
    public BitmapSource? LegendImage { get; init; }
    public IReadOnlyList<AxisGridLine> GridLines { get; init; } = Array.Empty<AxisGridLine>();
    public IReadOnlyList<AxisTick> XTickLabels { get; init; } = Array.Empty<AxisTick>();
    public IReadOnlyList<AxisTick> YTickLabels { get; init; } = Array.Empty<AxisTick>();
}

/// <summary>
/// One captured position rendered onto the zenith-dome view (see MosaicViewModel.RenderDome) -
/// its live Az/El at whatever DomeTimeUtc currently is, projected to a pixel and colour. Points
/// below the horizon at that moment are never created in the first place (see RenderDome), so
/// every DomeMarker that exists is actually visible in the sky right now.
/// </summary>
public record DomeMarker(string Label, double X, double Y, double Value, double ElevationDeg, double AzimuthDeg, Color Color, string Tooltip);

/// <summary>One concentric altitude ring on the dome, precomputed to a Canvas-friendly bounding box.</summary>
public record DomeRing(double Left, double Top, double Diameter);

/// <summary>One compass-rose label (N/NE/E/... ) on the dome, at its own pixel position.</summary>
public record DomeLabel(string Text, double X, double Y);

/// <summary>
/// Rendered state for the "Zenith Dome" tab - a from-here-right-now view, distinct from the
/// "Sky Mosaic" tab's persistent full-sky RA/Dec canvas (see MosaicViewModel.RenderDome for why
/// the two can't be the same view). Always a square canvas; markers/rings/spokes/labels are all
/// already in that same fixed pixel space, same convention MosaicHeatmapDisplay uses for the 2D
/// heatmap's own overlay.
/// </summary>
public class MosaicDomeDisplay
{
    public double CanvasSize { get; init; }
    public IReadOnlyList<DomeMarker> Markers { get; init; } = Array.Empty<DomeMarker>();
    public IReadOnlyList<DomeRing> AltitudeRings { get; init; } = Array.Empty<DomeRing>();
    public IReadOnlyList<AxisGridLine> AzimuthSpokes { get; init; } = Array.Empty<AxisGridLine>();
    public IReadOnlyList<DomeLabel> CompassLabels { get; init; } = Array.Empty<DomeLabel>();
    public string LegendMinText { get; init; } = string.Empty;
    public string LegendMaxText { get; init; } = string.Empty;
    public BitmapSource? LegendImage { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>
/// Backs the "Mosaic" tab in Visualise: points at a session folder (one baseline + many
/// dwell-point capture groups across positions), runs every position through the same
/// HiStreamingPipeline VisualiseViewModel.ProcessHiCore uses for a single file (via
/// MosaicProcessor), and renders the combined result as a sky-mosaic heatmap (RA/Dec x peak
/// power relative to the cold-sky baseline, in dB - see MosaicProcessor.FindLinePeak) and a
/// 3D surface (see MosaicSurfaceView, rendered as an actual globe, not a flat height-field) built
/// from a grid of the same shape. The 2D heatmap renders via HeatmapImageBuilder (a hand-rolled
/// BitmapSource) rather than LiveChartsCore's HeatSeries, which produced a blank chart against
/// real, well-spread session data - see HeatmapImageBuilder's remarks - as a sinusoidal
/// (Sanson-Flamsteed) equal-area projection (RenderSkyHeatmap's RowFactor* closures), matching
/// the same cos(Dec)/cos(Elevation) correction SweepPlanner.RowStepDeg applies to sweep spacing,
/// rather than a plain equirectangular RA/Az-vs-Dec/El grid whose real angular spacing would
/// otherwise shrink away from the equator/horizon while still being drawn at full width.
/// UseSmoothBlend switches HeatmapImageBuilder.Build (one flat
/// colour per measured cell, the default - each cell is a real independent measurement) for
/// HeatmapImageBuilder.BuildBlended (bilinear-interpolated between neighbouring cell centers,
/// for a continuous-looking gradient) on the 2D heatmap, and drives MosaicSurfaceView's own
/// bilinear grid subdivision on the 3D surface (see MosaicSurfaceView.Smooth) - one control,
/// both representations smooth together; all read already-cached grids, so toggling it
/// re-renders instantly without reprocessing the session. SurfaceMetric independently picks
/// which MosaicPosition field the 3D surface's height/colour represents - LineStrengthDb or
/// PeakVelocityKmPerSec (see MosaicSurfaceMetric) - while the 2D heatmap always shows
/// LineStrengthDb.
///
/// Two other visualisations were tried here and dropped: a stacked-line "waterfall" of every
/// position's spectrum (a live scrolling waterfall belongs to an actual capture in progress -
/// Capture, or Prepare while dwelling on the baseline - not a static multi-position
/// comparison), and a position-velocity heatmap (meaningful for a constant-Dec drift scan,
/// but its position axis has no physical meaning for scattered spot pointings across a 2D
/// sky area, which is the primary use case here).
/// </summary>
public partial class MosaicViewModel : ObservableObject
{
    private readonly MosaicProcessor _mosaicProcessor;
    private readonly LabSurveyMosaicProcessor _labSurveyMosaicProcessor;
    private readonly GridBuilder _gridBuilder;
    private readonly StatusBarViewModel _statusBar;
    private readonly TelescopeState _telescopeState;

    // The raw per-position list behind whichever grids are currently displayed - kept
    // separately from _lastStrengthGrid/_lastVelocityGrid because RenderDome needs each
    // position's own RA/Dec (or Az/El) to re-project it live, not a pre-binned RA/Dec cell.
    private IReadOnlyList<MosaicPosition>? _lastPositions;

    [ObservableProperty]
    private string? captureFolder;

    [ObservableProperty]
    private string? baselineFile;

    // Set by SelectFolder (a cheap extension/signature sniff - see MosaicFolderFormatDetector),
    // not by GenerateMosaicAsync itself - picking the folder just says what's in it; clicking
    // Generate Mosaic is still the thing that actually kicks off processing, same as before.
    // GenerateMosaicAsync branches on this to decide which processor to run (MosaicProcessor for
    // RastaFits, LabSurveyMosaicProcessor for LabSurveyText), so the exact same GridBuilder/
    // HeatmapImageBuilder/MosaicSurfaceView code downstream gets exercised either way.
    [ObservableProperty]
    private MosaicFolderFormat detectedFormat = MosaicFolderFormat.Empty;

    [ObservableProperty]
    private string detectedFormatDescription = string.Empty;

    /// <summary>
    /// True once a LAB Survey session is selected - hides the (not applicable) Baseline File
    /// row in MosaicView.xaml and switches the strength legend/unit text over to Kelvin (see
    /// StrengthUnitLabel) instead of dB.
    /// </summary>
    public bool IsLabSurveySource => DetectedFormat == MosaicFolderFormat.LabSurveyText;

    /// <summary>
    /// LineStrengthDb is a reused field for a LAB-sourced session - it holds a peak brightness
    /// temperature in Kelvin, not a true dB-relative-to-baseline figure (there's no baseline
    /// division for already-calibrated survey data - see LabSurveyMosaicProcessor's remarks).
    /// RenderSkyHeatmap/RenderSurface use this for the legend text so that distinction is
    /// visible rather than silently mislabelling Kelvin readings as dB.
    /// </summary>
    public string StrengthUnitLabel => IsLabSurveySource ? "K" : "dB";

    // Mirrored from VisualiseViewModel.TargetFftSize/SmoothingKind/SmoothingWindow/
    // DespikeEnabled/DespikeThresholdSigma (see their On...Changed partials) - deliberately
    // no separate controls on the Mosaic tab itself for any of these, so the intent is
    // "dial the Single Capture tab in until it looks right, then Generate Mosaic processes
    // every position in the session with those same settings" rather than a second set of
    // controls to keep in sync by hand.
    [ObservableProperty]
    private int targetFftSize;

    [ObservableProperty]
    private double integratedWindowKmPerSec = MosaicProcessor.DefaultIntegratedWindowKmPerSec;

    [ObservableProperty]
    private bool despikeEnabled;

    [ObservableProperty]
    private double despikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma;

    // Applied to MosaicProcessor.FindLinePeak's own search input (a smoothed copy of
    // RatioSpectrum), not just the stored MosaicPosition.HiSpectrum - unlike
    // HiStreamingPipeline.Process's single-file behaviour (which only ever smooths HiSpectrum,
    // see VisualiseViewModel.SmoothingKind's remarks), Mosaic's own displayed values
    // (LineStrengthDb/PeakVelocityKmPerSec) come from RatioSpectrum, so smoothing has to reach
    // that same array to actually change what the heatmap/globe show, rather than smoothing an
    // array nothing reads.
    [ObservableProperty]
    private SmoothingKind smoothingKind = SmoothingKind.None;

    [ObservableProperty]
    private int smoothingWindow = 21;

    // Matches the sweep's own angular separation (e.g. TargetRange.AngularSeparationDeg from
    // the plan that produced this session) so each rendered pixel is one real sky cell, not an
    // arbitrary subdivision of however much sky this one session happened to cover - see
    // GridBuilder.BuildGrid. Note GridBuilder itself still bins onto a uniform-coordinate (not
    // cos(dec)-corrected) full-sky canvas - see its own remarks - so this is an approximation
    // that's closest to correct near the cell's own declination/elevation.
    [ObservableProperty]
    private double skyCellSizeDeg = 5.0;

    [ObservableProperty]
    private string statusSummary = string.Empty;

    // Off by default - HeatmapImageBuilder.Build (one flat colour per measured cell) stays the
    // default rendering, unchanged for the 2D heatmap. Also governs the 3D surface's own
    // bilinear subdivision (see MosaicSurfaceView.Smooth) so one control smooths both
    // representations consistently. Toggling this re-renders the already-cached grid(s)
    // instead of reprocessing the session.
    [ObservableProperty]
    private bool useSmoothBlend;

    // Bound to MosaicSurfaceView.FlattenRelief - true renders the 3D globe as a plain sphere
    // (colour only, no radial bump/dent), matching what the real sky actually looks like from
    // near its center; false (default) keeps the literal terrain-relief visualization. Only
    // affects the 3D Surface tab - the 2D heatmap has no equivalent "relief" to flatten.
    [ObservableProperty]
    private bool flattenGlobeRelief;

    // Which MosaicPosition metric the 3D surface currently renders - see MosaicSurfaceMetric.
    [ObservableProperty]
    private MosaicSurfaceMetric surfaceMetric = MosaicSurfaceMetric.Strength;

    // The grids behind the currently-displayed heatmap/surface, kept so toggling
    // UseSmoothBlend/SurfaceMetric can re-render immediately instead of re-running
    // MosaicProcessor against the FITS files. Both are built together in BuildGrids since
    // they're cheap re-binnings of the same already-processed MosaicResult.
    private GridBuilder.MosaicGridResult? _lastStrengthGrid;
    private GridBuilder.MosaicGridResult? _lastVelocityGrid;

    public bool BaselineAvailable => BaselineFile is not null;

    /// <summary>
    /// The "Select Baseline FIT" button only makes sense for a RASTA FITS session - hidden for
    /// a LAB Survey (or empty/unrecognised) folder, same as the rest of the Baseline File row
    /// (see IsLabSurveySource), on top of its existing BaselineAvailable condition.
    /// </summary>
    public bool ShowSelectBaselineButton => !IsLabSurveySource && !BaselineAvailable;

    [ObservableProperty]
    private MosaicHeatmapDisplay skyHeatmap = new();

    // Feeds MosaicSurfaceView's HelixToolkit mesh - whichever of _lastStrengthGrid/
    // _lastVelocityGrid SurfaceMetric currently selects (see RenderSurface), consumed as a
    // height field instead of a flat colour map.
    [ObservableProperty]
    private double[,]? surfaceIntensityGrid;

    [ObservableProperty]
    private double[]? surfaceXValues; // RA hours or Az degrees

    [ObservableProperty]
    private double[]? surfaceYValues; // Dec or El degrees

    // Bound to MosaicSurfaceView.IsAltAz - which spherical convention it renders the globe with
    // (RA/Dec pole-to-pole vs Az/El horizon-to-zenith - see MosaicSurfaceView.Direction).
    [ObservableProperty]
    private bool surfaceIsAltAz;

    [ObservableProperty]
    private string surfaceLegendMinText = string.Empty;

    [ObservableProperty]
    private string surfaceLegendMaxText = string.Empty;

    // Real-valued (not pixel) axis ticks for MosaicSurfaceView's own floor grid/labels - see
    // RenderSurface. Position is an RA/Az value for XTicks, Dec/El for YTicks; MosaicSurfaceView
    // maps these into its normalized model space with the same NormX/NormZ it uses for the mesh.
    [ObservableProperty]
    private IReadOnlyList<AxisTick> surfaceXTicks = Array.Empty<AxisTick>();

    [ObservableProperty]
    private IReadOnlyList<AxisTick> surfaceYTicks = Array.Empty<AxisTick>();

    // ---------------------------------------------------------
    // Zenith Dome tab - a from-here-right-now Alt/Az view (see RenderDome), distinct from the
    // Sky Mosaic tab's persistent full-sky RA/Dec canvas above. DomeTimeUtc defaults to "now"
    // at construction and drives every position's live Az/El - editable so you can also see
    // what the sky looked like at an arbitrary moment, not just this instant.
    // ---------------------------------------------------------

    [ObservableProperty]
    private DateTime domeTimeUtc = DateTime.UtcNow;

    [ObservableProperty]
    private MosaicDomeDisplay skyDome = new();

    partial void OnDomeTimeUtcChanged(DateTime value) => RenderDome();

    [RelayCommand]
    private void SetDomeTimeNow() => DomeTimeUtc = DateTime.UtcNow;

    public ObservableCollection<MosaicPositionSummary> Positions { get; } = new();

    // Own progress/busy/status state for GenerateMosaicAsync, deliberately separate from
    // StatusBarViewModel.CaptureProgress/IsCaptureInProgress/CaptureStatus - those are also
    // driven by CaptureViewModel (and VisualiseViewModel's own Generate Chart), so mosaic
    // processing used to fight the same shared bar/text for ownership. Same pattern as
    // VisualiseViewModel's IsGenerating/GenerationProgress/GenerationStatus - see there for
    // the fuller rationale. Drives the Cancel Mosaic button that replaces "Generate Mosaic"
    // while a mosaic is being processed (see MosaicView.xaml), which doubles as the progress
    // indicator via GenerationProgress rather than a separate bar next to it.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateMosaicCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelMosaicCommand))]
    private bool isGenerating;

    [ObservableProperty]
    private double generationProgress;

    [ObservableProperty]
    private string generationStatus = string.Empty;

    // Only one GenerateMosaicAsync run at a time is ever in flight (GenerateMosaicCommand's
    // CanExecute enforces that), so a single field is enough for CancelMosaic to reach.
    // MosaicProcessor.ProcessFolderAsync already takes and observes a CancellationToken of
    // its own (checked once per position), so no change was needed there.
    private CancellationTokenSource? _generateCts;

    public MosaicViewModel(
        MosaicProcessor mosaicProcessor,
        LabSurveyMosaicProcessor labSurveyMosaicProcessor,
        GridBuilder gridBuilder,
        StatusBarViewModel statusBar,
        TelescopeState telescopeState)
    {
        _mosaicProcessor = mosaicProcessor;
        _labSurveyMosaicProcessor = labSurveyMosaicProcessor;
        _gridBuilder = gridBuilder;
        _statusBar = statusBar;
        _telescopeState = telescopeState;
    }

    // ---------------------------------------------------------
    // Progress reporting - same convention as VisualiseViewModel/
    // Calibrator/CaptureViewModel: real, measured progress, not
    // a time-based guess. Reported on this view model's own
    // GenerationProgress/GenerationStatus (see fields above),
    // not StatusBarViewModel's shared bar/text.
    // ---------------------------------------------------------

    private void BeginProgress(string status)
    {
        GenerationStatus = status;
        GenerationProgress = 0;
    }

    private void ReportProgress(double fraction)
    {
        GenerationProgress = Math.Clamp(fraction, 0.0, 1.0);
    }

    private void EndProgress()
    {
        GenerationProgress = 0;
    }

    [RelayCommand]
    private void SelectFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() != true)
            return;

        CaptureFolder = dlg.FolderName;

        // A cheap sniff only - deciding what to actually do with the folder's contents still
        // happens when Generate Mosaic is clicked (GenerateMosaicAsync), not here.
        DetectedFormat = MosaicFolderFormatDetector.Detect(CaptureFolder);
        UpdateDetectedFormatDescription();

        if (DetectedFormat == MosaicFolderFormat.RastaFits)
            AutoDetectBaseline();
        else
            BaselineFile = null; // not applicable/meaningless for a non-FITS or empty folder
    }

    private void UpdateDetectedFormatDescription()
    {
        DetectedFormatDescription = DetectedFormat switch
        {
            MosaicFolderFormat.RastaFits => "Detected: RASTA FITS captures.",
            MosaicFolderFormat.LabSurveyText =>
                "Detected: LAB Survey profile files (test data - no baseline needed; strength shown in Kelvin, not dB).",
            MosaicFolderFormat.Empty => "No .fits or LAB Survey .txt files found in this folder.",
            _ => "Folder contents not recognised as either RASTA FITS captures or LAB Survey profile files."
        };
        OnPropertyChanged(nameof(IsLabSurveySource));
        OnPropertyChanged(nameof(StrengthUnitLabel));
        OnPropertyChanged(nameof(ShowSelectBaselineButton));
    }

    /// <summary>
    /// Scans the newly selected folder for the "base_..." naming convention
    /// FitsPathBuilder.BuildCalibrationFilePath writes. Auto-selects it if exactly one is
    /// found; otherwise leaves BaselineFile for the user to set via the manual picker below.
    /// </summary>
    private void AutoDetectBaseline()
    {
        BaselineFile = null;

        if (CaptureFolder is null)
            return;

        try
        {
            var candidates = Directory.GetFiles(CaptureFolder, "*.fits")
                .Where(FitsPathBuilder.IsBaselineFile)
                .ToList();

            if (candidates.Count == 1)
                BaselineFile = candidates[0];
        }
        catch (IOException)
        {
            // Folder vanished/unreadable between picking it and scanning it - leave
            // BaselineFile null, the manual picker below still works.
        }
    }

    [RelayCommand]
    private void SelectBaselineFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "FITS files (*.fits)|*.fits" };
        if (dlg.ShowDialog() == true)
            BaselineFile = dlg.FileName;
    }

    [RelayCommand]
    private void ClearBaselineFile()
    {
        BaselineFile = null;
    }

    partial void OnBaselineFileChanged(string? value)
    {
        OnPropertyChanged(nameof(BaselineAvailable));
        OnPropertyChanged(nameof(ShowSelectBaselineButton));
    }

    partial void OnUseSmoothBlendChanged(bool value)
    {
        // The 3D surface's own subdivision smoothing is driven by MosaicSurfaceView's Smooth
        // binding directly (see MosaicView.xaml), so it re-renders on its own from this same
        // property change - only the 2D heatmap needs an explicit re-render call here.
        if (_lastStrengthGrid is not null)
            RenderSkyHeatmap(_lastStrengthGrid);
    }

    partial void OnSurfaceMetricChanged(MosaicSurfaceMetric value) => RenderSurface();

    private bool CanGenerateMosaic => !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanGenerateMosaic))]
    private async Task GenerateMosaicAsync()
    {
        if (CaptureFolder is null)
            return;
        if (DetectedFormat == MosaicFolderFormat.RastaFits && BaselineFile is null)
            return;
        if (DetectedFormat is not (MosaicFolderFormat.RastaFits or MosaicFolderFormat.LabSurveyText))
            return;

        _generateCts = new CancellationTokenSource();
        IsGenerating = true;

        BeginProgress("Processing mosaic…");
        try
        {
            void OnProgress(string status, double fraction)
            {
                GenerationStatus = status;
                ReportProgress(fraction);
            }

            // Same MosaicResult/MosaicPosition shape either way - everything downstream
            // (BuildGrids/BuildPositionsSummary and the GridBuilder/HeatmapImageBuilder/
            // MosaicSurfaceView code they feed) is identical regardless of which processor
            // actually produced it. That's the point: a LAB Survey session exercises the same
            // Sky Mosaic/3D Surface pipeline a real RASTA capture session would.
            var result = DetectedFormat == MosaicFolderFormat.LabSurveyText
                ? await _labSurveyMosaicProcessor.ProcessFolderAsync(
                    CaptureFolder,
                    IntegratedWindowKmPerSec,
                    OnProgress,
                    smoothing: SmoothingKind,
                    smoothingWindow: SmoothingWindow,
                    ct: _generateCts.Token)
                : await _mosaicProcessor.ProcessFolderAsync(
                    CaptureFolder,
                    BaselineFile!,
                    TargetFftSize,
                    IntegratedWindowKmPerSec,
                    OnProgress,
                    despike: DespikeEnabled,
                    despikeThresholdSigma: DespikeThresholdSigma,
                    smoothing: SmoothingKind,
                    smoothingWindow: SmoothingWindow,
                    ct: _generateCts.Token);

            BuildGrids(result);
            BuildPositionsSummary(result);

            StatusSummary = $"{result.Positions.Count} position(s) processed.";
            GenerationStatus = "Completed";
        }
        catch (OperationCanceledException)
        {
            GenerationStatus = "Cancelled.";
            StatusSummary = "Mosaic processing cancelled.";
        }
        finally
        {
            EndProgress();
            IsGenerating = false;
            _generateCts?.Dispose();
            _generateCts = null;
        }
    }

    // Cancels a running GenerateMosaicAsync. MosaicProcessor.ProcessFolderAsync processes
    // positions concurrently (Parallel.For) and stops scheduling new ones once this token is
    // cancelled, so whichever positions were already in flight (up to one per core) finish
    // before it actually returns - not instantly, and not per-chunk like VisualiseViewModel's
    // ForEachChunk, but bounded rather than running the rest of the whole session.
    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void CancelMosaic()
    {
        _generateCts?.Cancel();
        GenerationStatus = "Cancelling…";
    }

    /// <summary>
    /// Bins the just-processed session into both grids MosaicSurfaceMetric can select between -
    /// cheap re-binnings of the same MosaicResult, so both are always kept in sync with each
    /// other and ready for an instant SurfaceMetric/UseSmoothBlend toggle without reprocessing.
    /// </summary>
    private void BuildGrids(MosaicResult result)
    {
        _lastStrengthGrid = _gridBuilder.BuildGrid(result.Positions, SkyCellSizeDeg, p => p.LineStrengthDb);
        _lastVelocityGrid = _gridBuilder.BuildGrid(result.Positions, SkyCellSizeDeg, p => p.PeakVelocityKmPerSec);
        _lastPositions = result.Positions;
        RenderSkyHeatmap(_lastStrengthGrid);
        RenderSurface();
        RenderDome();
    }

    /// <summary>
    /// Renders the "Zenith Dome" tab: every position's live Az/El at DomeTimeUtc, from the
    /// site recorded on TelescopeState (the same site the app already tracks for the connected
    /// mount - see SettingsViewModel), projected onto a zenith-centered dome and coloured the
    /// same LineStrengthDb/StrengthUnitLabel ramp the 2D heatmap uses (HeatmapImageBuilder.Ramp).
    ///
    /// Deliberately NOT built from _lastStrengthGrid/GridBuilder - unlike the Sky Mosaic tab's
    /// persistent "coverage so far" RA/Dec canvas, an Az/El dome is only valid at one instant
    /// (the same RA/Dec sits at a different Az/El an hour later), so there's no meaningful sense
    /// in which it could accumulate across sessions - each position is re-projected fresh from
    /// its own stored RA/Dec (or, for an AltAz-mode session, its already-fixed Az/El) every time
    /// this runs, whether that's after a fresh Generate Mosaic or just DomeTimeUtc changing.
    ///
    /// Compass orientation matches how a naked-eye sky chart (e.g. Cartes du Ciel's Alt/Az
    /// view) is drawn, not a ground map: N up, S down, E LEFT, W RIGHT - looking up at the
    /// inside of the sky's dome mirrors east/west relative to looking down at a map. Derived by
    /// projecting azimuth clockwise-negated (screenTheta = -azimuth) so Az=90 (E) lands left of
    /// centre and Az=270 (W) lands right - see the pixel formula below.
    ///
    /// Only positions currently above the horizon (ElevationDeg >= 0) are plotted at all - a
    /// position below the horizon right now genuinely isn't part of "the sky as it looks from
    /// here right now", the dome's whole premise, rather than something to grey out.
    /// </summary>
    private void RenderDome()
    {
        if (_lastPositions is null || _lastPositions.Count == 0)
        {
            SkyDome = new MosaicDomeDisplay { StatusText = "No mosaic processed yet." };
            return;
        }

        const double canvasSize = 640;
        const double margin = 50; // room for compass labels outside the dome circle
        double cx = canvasSize / 2.0;
        double cy = canvasSize / 2.0;
        double domeRadius = canvasSize / 2.0 - margin;

        double siteLatDeg = _telescopeState.SiteLatitudeDeg;
        double siteLonDeg = _telescopeState.SiteLongitudeDeg;

        (double x, double y) Project(double azDeg, double elDeg)
        {
            double r = Math.Clamp((90.0 - elDeg) / 90.0, 0.0, 1.0) * domeRadius;
            double azRad = azDeg * Math.PI / 180.0;
            return (cx - r * Math.Sin(azRad), cy - r * Math.Cos(azRad));
        }

        var raw = new List<(string label, double x, double y, double value, double elDeg, double azDeg)>();
        foreach (var p in _lastPositions)
        {
            if (double.IsNaN(p.LineStrengthDb))
                continue;

            double azDeg, elDeg;
            if (p.Mode == CoordinateMode.Equatorial && p.RaHours.HasValue && p.DecDeg.HasValue)
            {
                (azDeg, elDeg) = AstronomyUtils.EquatorialToHorizontal(p.RaHours.Value, p.DecDeg.Value, DomeTimeUtc, siteLatDeg, siteLonDeg);
            }
            else if (p.Mode == CoordinateMode.AltAz && p.AzDeg.HasValue && p.AltDeg.HasValue)
            {
                azDeg = p.AzDeg.Value;
                elDeg = p.AltDeg.Value;
            }
            else
            {
                continue;
            }

            if (elDeg < 0)
                continue; // below the horizon right now - not part of "the sky, from here, now"

            var (x, y) = Project(azDeg, elDeg);
            raw.Add((p.Label, x, y, p.LineStrengthDb, elDeg, azDeg));
        }

        double min = raw.Count > 0 ? raw.Min(m => m.value) : 0;
        double max = raw.Count > 0 ? raw.Max(m => m.value) : 1;
        double range = Math.Max(max - min, 1e-9);

        var markers = raw.Select(m =>
        {
            var (r, g, b) = HeatmapImageBuilder.Ramp((m.value - min) / range);
            string tooltip = $"{m.label}\nAz {m.azDeg:F1}°  El {m.elDeg:F1}°\n{m.value:F1} {StrengthUnitLabel}";
            return new DomeMarker(m.label, m.x, m.y, m.value, m.elDeg, m.azDeg, Color.FromRgb(r, g, b), tooltip);
        }).ToList();

        var rings = new List<DomeRing>();
        for (double el = 0; el < 90; el += 15)
        {
            double r = (90.0 - el) / 90.0 * domeRadius;
            rings.Add(new DomeRing(cx - r, cy - r, r * 2));
        }

        var spokes = new List<AxisGridLine>();
        var labels = new List<DomeLabel>();
        string[] compassPoints = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        for (int i = 0; i < 12; i++)
        {
            double az = i * 30.0;
            var (ex, ey) = Project(az, 0.0);
            spokes.Add(new AxisGridLine(cx, cy, ex, ey));
        }
        for (int i = 0; i < compassPoints.Length; i++)
        {
            double az = i * 45.0;
            double azRad = az * Math.PI / 180.0;
            double labelR = domeRadius + 20;
            labels.Add(new DomeLabel(compassPoints[i], cx - labelR * Math.Sin(azRad), cy - labelR * Math.Cos(azRad)));
        }

        SkyDome = new MosaicDomeDisplay
        {
            CanvasSize = canvasSize,
            Markers = markers,
            AltitudeRings = rings,
            AzimuthSpokes = spokes,
            CompassLabels = labels,
            LegendMinText = raw.Count == 0 ? "n/a" : $"{min:F1} {StrengthUnitLabel}",
            LegendMaxText = raw.Count == 0 ? "n/a" : $"{max:F1} {StrengthUnitLabel}",
            LegendImage = HeatmapImageBuilder.BuildLegendStrip(200),
            StatusText = raw.Count == 0
                ? "None of this session's positions are above the horizon at the selected time."
                : $"{raw.Count} of {_lastPositions.Count} position(s) above the horizon at {DomeTimeUtc:yyyy-MM-dd HH:mm} UTC."
        };
    }

    /// <summary>
    /// Re-renders the 2D heatmap/legend from an already-built GridBuilder.MosaicGridResult -
    /// split out from BuildGrids so toggling UseSmoothBlend can call this directly against
    /// _lastStrengthGrid instead of re-running MosaicProcessor against the session's FITS files.
    /// Always LineStrengthDb - see MosaicSurfaceMetric's remarks on why the 2D map doesn't
    /// follow the 3D surface's metric toggle.
    /// </summary>
    private void RenderSkyHeatmap(GridBuilder.MosaicGridResult grid)
    {
        bool altAz = grid.Mode == CoordinateMode.AltAz;
        var (min, max) = FindRange(grid.IntensityGrid);
        var (pixelWidth, pixelHeight) = SizeImageForGrid(grid.IntensityGrid);

        var (gridLines, xLabels, yLabels) = BuildPixelAxisOverlay(grid, altAz, pixelWidth, pixelHeight);

        // Sinusoidal (Sanson-Flamsteed) equal-area projection: RA/Azimuth circles are physically
        // smaller away from the equator/horizon (cos(Dec) or cos(Elevation) - same correction
        // SweepPlanner.RowStepDeg applies to sweep spacing), so each row's rendered width is
        // compressed by that same factor rather than stretched flat across the full image width -
        // see HeatmapImageBuilder.Build's remarks. AxisYCenters already holds Dec-or-El degrees
        // either way, so one formula covers both CoordinateModes.
        double cellSizeYForRows = grid.AxisYCenters.Length > 1 ? grid.AxisYCenters[1] - grid.AxisYCenters[0] : 1;
        double minYForRows = grid.AxisYCenters[0] - cellSizeYForRows / 2;
        double RowFactorAtIndex(int gy) => Math.Cos(grid.AxisYCenters[gy] * Math.PI / 180.0);
        double RowFactorContinuous(double gyF) =>
            Math.Cos((minYForRows + (gyF + 0.5) * cellSizeYForRows) * Math.PI / 180.0);

        SkyHeatmap = new MosaicHeatmapDisplay
        {
            Image = UseSmoothBlend
                ? HeatmapImageBuilder.BuildBlended(grid.IntensityGrid, pixelWidth, pixelHeight, flipY: true, RowFactorContinuous)
                : HeatmapImageBuilder.Build(grid.IntensityGrid, pixelWidth, pixelHeight, flipY: true, RowFactorAtIndex),
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            XAxisLabel = altAz ? "Azimuth" : "RA",
            YAxisLabel = altAz ? "Elevation" : "Dec",
            LegendMinText = double.IsNaN(min) ? "n/a" : $"{min:F1} {StrengthUnitLabel}",
            LegendMaxText = double.IsNaN(max) ? "n/a" : $"{max:F1} {StrengthUnitLabel}",
            LegendImage = HeatmapImageBuilder.BuildLegendStrip(200),
            GridLines = gridLines,
            XTickLabels = xLabels,
            YTickLabels = yLabels
        };
    }

    /// <summary>
    /// Computes "nice" axis ticks (see AxisTicks.ComputeNiceTicks) over the grid's full plotted
    /// extent (cell 0's outer edge to the last cell's outer edge - half a cell wider each side
    /// than the cell-center range the old XAxisLabel/YAxisLabel range text used) and maps each
    /// to a pixel position in the same coordinate space as the heatmap Image, so
    /// MosaicView.xaml's overlay ItemsControls can position Lines/TextBlocks with plain
    /// one-to-one bindings - no runtime ActualWidth/ActualHeight dependency, since pixelWidth/
    /// pixelHeight are already fixed at build time. Y uses the heatmap's own flipY=true
    /// convention (Dec/Alt increases upward, i.e. toward pixel row 0).
    ///
    /// Gridlines trace the same sinusoidal silhouette RenderSkyHeatmap's row-compression gives
    /// the rendered image (see HeatmapImageBuilder.Build's remarks): a meridian (constant RA/Az)
    /// is a curved polyline, sampled every few degrees of Dec/El, rather than one straight
    /// vertical line; a parallel (constant Dec/El) is shortened to that row's own compressed
    /// width instead of spanning the full image. Tick label positions are unaffected - the X
    /// label strip sits in its own row below the plot (an axis caption, not a point on the curve
    /// itself), and the Y label strip already only needs the same linear Dec/El-to-pixel-row
    /// mapping the parallels themselves use.
    /// </summary>
    private static (List<AxisGridLine> gridLines, List<AxisTick> xLabels, List<AxisTick> yLabels) BuildPixelAxisOverlay(
        GridBuilder.MosaicGridResult grid, bool altAz, int pixelWidth, int pixelHeight)
    {
        var gridLines = new List<AxisGridLine>();
        var xLabels = new List<AxisTick>();
        var yLabels = new List<AxisTick>();

        double cellSizeX = grid.AxisXCenters.Length > 1 ? grid.AxisXCenters[1] - grid.AxisXCenters[0] : 1;
        double cellSizeY = grid.AxisYCenters.Length > 1 ? grid.AxisYCenters[1] - grid.AxisYCenters[0] : 1;
        double minX = grid.AxisXCenters[0] - cellSizeX / 2;
        double maxX = grid.AxisXCenters[^1] + cellSizeX / 2;
        double minY = grid.AxisYCenters[0] - cellSizeY / 2;
        double maxY = grid.AxisYCenters[^1] + cellSizeY / 2;
        double xRange = Math.Max(maxX - minX, 1e-9);
        double yRange = Math.Max(maxY - minY, 1e-9);

        double RowFactor(double decOrElDeg) => Math.Cos(decOrElDeg * Math.PI / 180.0);
        double PyFor(double decOrElDeg) => pixelHeight - (decOrElDeg - minY) / yRange * pixelHeight;

        const int meridianSteps = 24;
        foreach (double tick in AxisTicks.ComputeNiceTicks(minX, maxX))
        {
            double u = (tick - minX) / xRange * 2 - 1; // -1..+1, matching HeatmapImageBuilder's own u
            double? prevPx = null, prevPy = null;
            for (int i = 0; i <= meridianSteps; i++)
            {
                double decOrEl = minY + (maxY - minY) * i / meridianSteps;
                double px = pixelWidth / 2.0 * (1 + u * RowFactor(decOrEl));
                double py = PyFor(decOrEl);
                if (prevPx is not null)
                    gridLines.Add(new AxisGridLine(prevPx.Value, prevPy!.Value, px, py));
                prevPx = px; prevPy = py;
            }
            xLabels.Add(new AxisTick(FormatAxisValue(tick, isXAxis: true, altAz), pixelWidth / 2.0 * (1 + u)));
        }

        foreach (double tick in AxisTicks.ComputeNiceTicks(minY, maxY))
        {
            double factor = RowFactor(tick);
            double py = PyFor(tick);
            gridLines.Add(new AxisGridLine(pixelWidth / 2.0 * (1 - factor), py, pixelWidth / 2.0 * (1 + factor), py));
            yLabels.Add(new AxisTick(FormatAxisValue(tick, isXAxis: false, altAz), py - 6));
        }

        return (gridLines, xLabels, yLabels);
    }

    /// <summary>RA in hours, Dec signed degrees, Az/El unsigned degrees.</summary>
    private static string FormatAxisValue(double value, bool isXAxis, bool altAz)
    {
        if (altAz)
            return $"{value:F0}°";
        return isXAxis ? $"{value:F1}h" : $"{value.ToString("+0;-0;0")}°";
    }

    /// <summary>
    /// Feeds MosaicSurfaceView from whichever of _lastStrengthGrid/_lastVelocityGrid
    /// SurfaceMetric currently selects - split out so both the metric toggle and a fresh
    /// BuildGrids can reach it without duplicating the grid-picking logic.
    /// </summary>
    private void RenderSurface()
    {
        var grid = SurfaceMetric == MosaicSurfaceMetric.Velocity ? _lastVelocityGrid : _lastStrengthGrid;
        if (grid is null)
            return;

        bool altAz = grid.Mode == CoordinateMode.AltAz;
        var (min, max) = FindRange(grid.IntensityGrid);
        string unit = SurfaceMetric == MosaicSurfaceMetric.Velocity ? "km/s" : StrengthUnitLabel;

        SurfaceIntensityGrid = grid.IntensityGrid;
        SurfaceXValues = grid.AxisXCenters;
        SurfaceYValues = grid.AxisYCenters;
        SurfaceIsAltAz = altAz;
        SurfaceLegendMinText = double.IsNaN(min) ? "n/a" : $"{min:F1} {unit}";
        SurfaceLegendMaxText = double.IsNaN(max) ? "n/a" : $"{max:F1} {unit}";

        double cellSizeX = grid.AxisXCenters.Length > 1 ? grid.AxisXCenters[1] - grid.AxisXCenters[0] : 1;
        double cellSizeY = grid.AxisYCenters.Length > 1 ? grid.AxisYCenters[1] - grid.AxisYCenters[0] : 1;
        double minX = grid.AxisXCenters[0] - cellSizeX / 2;
        double maxX = grid.AxisXCenters[^1] + cellSizeX / 2;
        double minY = grid.AxisYCenters[0] - cellSizeY / 2;
        double maxY = grid.AxisYCenters[^1] + cellSizeY / 2;

        SurfaceXTicks = AxisTicks.ComputeNiceTicks(minX, maxX)
            .Select(v => new AxisTick(FormatAxisValue(v, isXAxis: true, altAz), v)).ToList();
        SurfaceYTicks = AxisTicks.ComputeNiceTicks(minY, maxY)
            .Select(v => new AxisTick(FormatAxisValue(v, isXAxis: false, altAz), v)).ToList();
    }

    private void BuildPositionsSummary(MosaicResult result)
    {
        Positions.Clear();
        foreach (var p in result.Positions)
        {
            string coords = p.Mode switch
            {
                CoordinateMode.Equatorial => $"RA {p.RaHours:F2}h  Dec {p.DecDeg:F1}°",
                CoordinateMode.AltAz => $"Az {p.AzDeg:F1}°  El {p.AltDeg:F1}°",
                _ => "n/a"
            };
            Positions.Add(new MosaicPositionSummary(
                p.Label, coords, p.LineStrengthDb, p.PeakVelocityKmPerSec, p.SourceFiles.Count));
        }
    }

    /// <summary>
    /// Renders each grid cell as an equal-size square block of pixels, so a full-sky grid's
    /// real aspect ratio (RA 0-24h over Dec -90..+90 is 2:1 in sky-angle terms - 360 deg of RA
    /// against 180 deg of Dec) comes through in the image rather than being squashed into an
    /// arbitrary fixed square. Caps the overall bitmap size for very fine cell sizes/large grids.
    /// </summary>
    private static (int width, int height) SizeImageForGrid(double[,] grid)
    {
        const int pixelsPerCell = 8;
        const int maxDimension = 1600;

        int width = grid.GetLength(0) * pixelsPerCell;
        int height = grid.GetLength(1) * pixelsPerCell;

        if (width > maxDimension || height > maxDimension)
        {
            double scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
            width = Math.Max(1, (int)(width * scale));
            height = Math.Max(1, (int)(height * scale));
        }

        return (width, height);
    }

    /// <summary>Min/max over a grid's non-NaN cells, or (NaN, NaN) if every cell is NaN.</summary>
    private static (double min, double max) FindRange(double[,] grid)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (double v in grid)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return min <= max ? (min, max) : (double.NaN, double.NaN);
    }
}
