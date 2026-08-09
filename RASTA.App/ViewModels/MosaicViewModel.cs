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
public record MosaicPositionSummary(string Label, string Coordinates, double LineStrengthDb, int FileCount);

/// <summary>
/// One rendered heatmap panel's worth of display state - always replaced as a whole new
/// instance on rebuild (rather than mutated in place) so a single property-changed
/// notification on the containing MosaicViewModel property refreshes every nested binding.
/// </summary>
public class MosaicHeatmapDisplay
{
    public BitmapSource? Image { get; init; }
    public string XAxisLabel { get; init; } = string.Empty;
    public string YAxisLabel { get; init; } = string.Empty;
    public string LegendMinText { get; init; } = string.Empty;
    public string LegendMaxText { get; init; } = string.Empty;
    public BitmapSource? LegendImage { get; init; }
}

/// <summary>
/// Backs the "Mosaic" tab in Visualise: points at a session folder (one baseline + many
/// dwell-point capture groups across positions), runs every position through the same
/// HiStreamingPipeline VisualiseViewModel.ProcessHiCore uses for a single file (via
/// MosaicProcessor), and renders the combined result as a sky-mosaic heatmap (RA/Dec x peak
/// power relative to the cold-sky baseline, in dB - see MosaicProcessor.ComputeLineStrengthDb)
/// and a 3D surface (see MosaicSurfaceView) built from that same grid. The heatmap renders via
/// HeatmapImageBuilder (a hand-rolled BitmapSource) rather than LiveChartsCore's HeatSeries,
/// which produced a blank chart against real, well-spread session data - see
/// HeatmapImageBuilder's remarks. UseSmoothBlend switches HeatmapImageBuilder.Build (one flat
/// colour per measured cell, the default - each cell is a real independent measurement) for
/// HeatmapImageBuilder.BuildBlended (bilinear-interpolated between neighbouring cell centers,
/// for a continuous-looking gradient); both read the same cached grid, so toggling it re-renders
/// instantly without reprocessing the session.
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
    // default rendering, unchanged. Toggling this on re-renders the already-cached grid via
    // HeatmapImageBuilder.BuildBlended instead of reprocessing the session.
    [ObservableProperty]
    private bool useSmoothBlend;

    // The grid behind the currently-displayed heatmap/surface, kept so toggling UseSmoothBlend
    // can re-render immediately instead of re-running MosaicProcessor against the FITS files.
    private GridBuilder.MosaicGridResult? _lastGrid;

    public bool BaselineAvailable => BaselineFile is not null;

    [ObservableProperty]
    private MosaicHeatmapDisplay skyHeatmap = new();

    // Feeds MosaicSurfaceView's HelixToolkit mesh - the same RA/Dec x LineStrengthDb grid as
    // the sky heatmap above, just consumed as a height field instead of a flat colour map.
    [ObservableProperty]
    private double[,]? surfaceIntensityGrid;

    [ObservableProperty]
    private double[]? surfaceXValues; // RA hours or Az degrees

    [ObservableProperty]
    private double[]? surfaceYValues; // Dec or El degrees

    public ObservableCollection<MosaicPositionSummary> Positions { get; } = new();

    public MosaicViewModel(MosaicProcessor mosaicProcessor, GridBuilder gridBuilder, StatusBarViewModel statusBar)
    {
        _mosaicProcessor = mosaicProcessor;
        _gridBuilder = gridBuilder;
        _statusBar = statusBar;
    }

    // ---------------------------------------------------------
    // Progress reporting - same convention as VisualiseViewModel/
    // Calibrator/CaptureViewModel: real, measured progress, not
    // a time-based guess.
    // ---------------------------------------------------------

    private void BeginProgress(string status)
    {
        _statusBar.CaptureStatus = status;
        _statusBar.CaptureProgress = 0;
        _statusBar.IsCaptureInProgress = true;
    }

    private void ReportProgress(double fraction)
    {
        _statusBar.CaptureProgress = Math.Clamp(fraction, 0.0, 1.0);
    }

    private void EndProgress()
    {
        _statusBar.IsCaptureInProgress = false;
        _statusBar.CaptureProgress = 0;
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
        if (_lastGrid is not null)
            RenderSkyHeatmap(_lastGrid);
    }

    [RelayCommand]
    private async Task GenerateMosaicAsync()
    {
        if (CaptureFolder is null || BaselineFile is null)
            return;

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
                    _statusBar.CaptureStatus = status;
                    ReportProgress(fraction);
                },
                despike: DespikeEnabled,
                despikeThresholdSigma: DespikeThresholdSigma);

            BuildSkyHeatmap(result);
            BuildPositionsSummary(result);

            StatusSummary = $"{result.Positions.Count} position(s) processed.";
            _statusBar.CaptureStatus = "Completed";
        }
        finally
        {
            EndProgress();
        }
    }

    private void BuildSkyHeatmap(MosaicResult result)
    {
        var grid = _gridBuilder.BuildGrid(result.Positions, SkyCellSizeDeg);
        _lastGrid = grid;
        RenderSkyHeatmap(grid);
    }

    /// <summary>
    /// Re-renders the heatmap/legend/3D-surface grid from an already-built
    /// GridBuilder.MosaicGridResult - split out from BuildSkyHeatmap so toggling
    /// UseSmoothBlend can call this directly against _lastGrid instead of re-running
    /// MosaicProcessor against the session's FITS files.
    /// </summary>
    private void RenderSkyHeatmap(GridBuilder.MosaicGridResult grid)
    {
        bool altAz = grid.Mode == CoordinateMode.AltAz;
        var (min, max) = FindRange(grid.IntensityGrid);
        var (pixelWidth, pixelHeight) = SizeImageForGrid(grid.IntensityGrid);

        SkyHeatmap = new MosaicHeatmapDisplay
        {
            Image = UseSmoothBlend
                ? HeatmapImageBuilder.BuildBlended(grid.IntensityGrid, pixelWidth, pixelHeight, flipY: true)
                : HeatmapImageBuilder.Build(grid.IntensityGrid, pixelWidth, pixelHeight, flipY: true),
            XAxisLabel = altAz
                ? $"Azimuth: {grid.AxisXCenters[0]:F1}° → {grid.AxisXCenters[^1]:F1}°"
                : $"RA: {grid.AxisXCenters[0]:F1}h → {grid.AxisXCenters[^1]:F1}h",
            YAxisLabel = altAz
                ? $"Elevation: {grid.AxisYCenters[0]:F1}° → {grid.AxisYCenters[^1]:F1}°"
                : $"Dec: {grid.AxisYCenters[0]:F1}° → {grid.AxisYCenters[^1]:F1}°",
            LegendMinText = double.IsNaN(min) ? "n/a" : $"{min:F1} dB",
            LegendMaxText = double.IsNaN(max) ? "n/a" : $"{max:F1} dB",
            LegendImage = HeatmapImageBuilder.BuildLegendStrip(200)
        };

        // The 3D surface renders this exact grid as a height field - same positions, same
        // LineStrengthDb values, just RA/Dec on the plane and dB as height instead of colour.
        SurfaceIntensityGrid = grid.IntensityGrid;
        SurfaceXValues = grid.AxisXCenters;
        SurfaceYValues = grid.AxisYCenters;
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
            Positions.Add(new MosaicPositionSummary(p.Label, coords, p.LineStrengthDb, p.SourceFiles.Count));
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
