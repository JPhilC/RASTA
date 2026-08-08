using RASTA.Core.Calibration;
using System.Windows;
using System.Windows.Controls;

namespace RASTA.App.Views
{
    /// <summary>
    /// Row shown for one ColdSkyCandidate - formats the raw values into display strings so the
    /// XAML DataTemplate stays free of value converters. Built fresh each time the window is
    /// shown (see ColdSkySelectionWindow constructor).
    /// </summary>
    public sealed class ColdSkyCandidateRow
    {
        public ColdSkyCandidate Candidate { get; }
        public string Label { get; }
        public string AzAltText { get; }
        public string RaDecText { get; }
        public string GalacticText { get; }

        public ColdSkyCandidateRow(ColdSkyCandidate candidate, int index)
        {
            Candidate = candidate;
            Label = $"Option {index + 1} — {CompassDirection(candidate.AzimuthDeg)}";
            AzAltText = $"Az {candidate.AzimuthDeg:F1}°, Alt {candidate.ElevationDeg:F1}°";

            double raHours = candidate.RightAscensionHours;
            int raH = (int)raHours;
            int raM = (int)Math.Round((raHours - raH) * 60.0);
            char decSign = candidate.DeclinationDeg < 0 ? '-' : '+';
            double absDec = Math.Abs(candidate.DeclinationDeg);
            int decD = (int)absDec;
            int decM = (int)Math.Round((absDec - decD) * 60.0);
            RaDecText = $"RA {raH:D2}h{raM:D2}m, Dec {decSign}{decD:D2}°{decM:D2}'";

            double absB = Math.Abs(candidate.GalacticLatitudeDeg);
            string quality = absB >= 40 ? "very cold" : absB >= 25 ? "cold" : "moderate";
            GalacticText = $"Galactic b = {candidate.GalacticLatitudeDeg:F1}° ({quality})";
        }

        private static string CompassDirection(double azimuthDeg)
        {
            string[] points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            int index = (int)Math.Round(azimuthDeg / 45.0) % 8;
            return points[index];
        }
    }

    public partial class ColdSkySelectionWindow : Window
    {
        public ColdSkyCandidate? SelectedCandidate { get; private set; }

        public ColdSkySelectionWindow(IReadOnlyList<ColdSkyCandidate> candidates)
        {
            InitializeComponent();

            CandidateList.ItemsSource = candidates
                .Select((c, i) => new ColdSkyCandidateRow(c, i))
                .ToList();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ColdSkyCandidateRow row })
            {
                SelectedCandidate = row.Candidate;
                DialogResult = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedCandidate = null;
            DialogResult = false;
        }
    }
}
