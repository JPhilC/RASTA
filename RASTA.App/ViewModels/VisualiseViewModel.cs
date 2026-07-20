using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Capture;
using RASTA.Processing.VisualisationData;
using RASTA.Processing.Gridding;

public partial class VisualiseViewModel : ObservableObject
{
    private readonly SpectrumImageBuilder _spectrumBuilder;
    private readonly HeatmapBuilder _heatmapBuilder;
    private readonly GridBuilder _gridBuilder;

    [ObservableProperty]
    private BitmapSource? spectrumImage;

    [ObservableProperty]
    private BitmapSource? heatmapImage;

    [ObservableProperty]
    private bool useGridding = true;

    public VisualiseViewModel(
        SpectrumImageBuilder spectrumBuilder,
        HeatmapBuilder heatmapBuilder,
        GridBuilder gridBuilder)
    {
        _spectrumBuilder = spectrumBuilder;
        _heatmapBuilder = heatmapBuilder;
        _gridBuilder = gridBuilder;
    }

    public void LoadObservation(ObservationRecord record)
    {
        SpectrumImage = _spectrumBuilder.BuildSpectrumImage(record);
        HeatmapImage = _heatmapBuilder.BuildHeatmapImage(new[] { record });
    }

    public void LoadObservations(IEnumerable<ObservationRecord> records)
    {
        var list = records.ToList();
        if (list.Count == 0)
            return;

        SpectrumImage = _spectrumBuilder.BuildSpectrumImage(list[0]);

        if (!UseGridding)
        {
            HeatmapImage = _heatmapBuilder.BuildHeatmapImage(list);
            return;
        }

        var grid = _gridBuilder.BuildGrid(list, gridWidth: 100, gridHeight: 100);
        HeatmapImage = _heatmapBuilder.BuildHeatmapImage(grid.IntensityGrid);
    }
}
