using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Helpers;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Processing.Gridding;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.Mosaic;
using System.Collections.ObjectModel;
using System.IO;
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
/// Backs the "Mosaic" tab in Visualise: points at a session folder (one baseline + many
/// dwell-point capture groups across positions), runs every position through the same
/// HiStreamingPipeline VisualiseViewModel.ProcessHiCore uses for a single file (via
/// MosaicProcessor), and renders the combined result as a sky-mosaic heatmap (RA/Dec x peak
/// power relative to the cold-sky baseline, in dB - see MosaicProcessor.FindLinePeak) and a
/// 3D surface (see MosaicSurfaceView) built from a grid of the same shape. The heatmap renders
/// via HeatmapImageBuilder (a hand-rolled BitmapSource) rather than LiveChartsCore's HeatSeries,
/// which produced a blank chart against real, well-spread session data - see
/// HeatmapImageBuilder's remarks. UseSmoothBlend switches HeatmapImageBuilder.Build (one flat
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
    private readonly GridBuilder _gridBuilder;
    private readonly StatusBarViewModel _statusBar;

    [ObservableProperty]
    private string? captureFolder;

    [ObservableProperty]
    private string? baselineFile;

    [ObservableProperty]
    private int targetFftSize;

    [ObservableProperty]
    private double integratedWindowKmPerSec = MosaicProcessor.DefaultIntegratedWindowKmPerSec;

    // Mirrored from VisualiseViewModel.DespikeEnabled/DespikeThresholdSigma (see their
    // On...Changed partials) - deliberately no separate controls on the Mosaic tab itself,
    // so the ones on the main Visualise view govern both.
    [ObservableProperty]
    private bool despikeEnabled;

    [ObservableProperty]
    private double despikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma;

    // Matches the sweep's own step size (e.g. TargetRange.StepDeg from the plan that produced
    // this session) so each rendered pixel is one real sky cell, not an arbitrary subdivision
    // of however much sky this one session happened to cover - see GridBuilder.BuildGrid.
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

    public MosaicViewModel(MosaicProcessor mosaicProcessor, GridBuilder gridBuilder, StatusBarViewModel statusBar)
    {
        _mosaicProcessor = mosaicProcessor;
        _gridBuilder = gridBuilder;
        _statusBar = statusBar;
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
        AutoDetectBaseline();
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

    partial void OnBaselineFileChanged(string? value) => OnPropertyChanged(nameof(BaselineAvailable));

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
        if (CaptureFolder is null || BaselineFile is null)
            return;

        _generateCts = new CancellationTokenSource();
        IsGenerating = true;

        BeginProgress("Processing mosaic…");
        try
        {
            var result = await _mosaicProcessor.ProcessFolderAsync(
                CaptureFolder,
                BaselineFile,
                TargetFftSize,
                IntegratedWindowKmPerSec,
                (status, fraction) =>
                {
                    GenerationStatus = status;
                    ReportProgress(fraction);
                },
                despike: DespikeEnabled,
                despikeThresholdSigma: DespikeThresholdSigma,
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

    // Cancels a running GenerateMosaicAsync. MosaicProcessor.ProcessFolderAsync checks the
    // token once per position (between whole capture groups, not per-chunk like
    // VisualiseViewModel's ForEachChunk) so cancellation takes effect at the next position
    // boundary rather than instantly mid-FFT.
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
        RenderSkyHeatmap(_lastStrengthGrid);
        RenderSurface();
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

        SkyHeatmap = new MosaicHeatmapDisplay
        {
            Image = UseSmoothBlend
                ? HeatmapImageBuilder.BuildBlended(grid.IntensityGrid, pixelWidth, pixelHeight, flipY: true)
                : HeatmapImageBuilder.Build(grid.IntensityGrid, pixelWidth, pixelHeight, flipY: true),
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            XAxisLabel = altAz ? "Azimuth" : "RA",
            YAxisLabel = altAz ? "Elevation" : "Dec",
            LegendMinText = double.IsNaN(min) ? "n/a" : $"{min:F1} dB",
            LegendMaxText = double.IsNaN(max) ? "n/a" : $"{max:F1} dB",
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

        foreach (double tick in AxisTicks.ComputeNiceTicks(minX, maxX))
        {
            double px = (tick - minX) / xRange * pixelWidth;
            gridLines.Add(new AxisGridLine(px, 0, px, pixelHeight));
            xLabels.Add(new AxisTick(FormatAxisValue(tick, isXAxis: true, altAz), px));
        }

        foreach (double tick in AxisTicks.ComputeNiceTicks(minY, maxY))
        {
            double py = pixelHeight - (tick - minY) / yRange * pixelHeight;
            gridLines.Add(new AxisGridLine(0, py, pixelWidth, py));
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
        string unit = SurfaceMetric == MosaicSurfaceMetric.Velocity ? "km/s" : "dB";

        SurfaceIntensityGrid = grid.IntensityGrid;
        SurfaceXValues = grid.AxisXCenters;
        SurfaceYValues = grid.AxisYCenters;
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
