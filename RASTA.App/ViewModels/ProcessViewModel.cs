using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.Core.Capture;
using RASTA.Processing.Spectral;
using System.Diagnostics;

namespace RASTA.App.ViewModels;

public partial class ProcessViewModel : ObservableObject
{
    private readonly SpectrumMath _math;

    [ObservableProperty]
    private List<ObservationRecord>? processed;

    public ProcessViewModel(SpectrumMath math)
    {
        _math = math;
    }

    [RelayCommand]
    private void Process(IEnumerable<ObservationRecord> records)
    {
        var list = new List<ObservationRecord>();

        foreach (var r in records)
        {
            var baseline = _math.SubtractBaseline(r.AveragedSpectrum);
            var smooth = _math.Smooth(baseline);
            var norm = _math.Normalise(smooth);

            list.Add(new ObservationRecord
            {
                AveragedSpectrum = norm,
                Metadata = r.Metadata
            });
        }

        Processed = list;
    }
}
