using RASTA.App.Views;
using RASTA.Core.Calibration;
using System.Windows;

namespace RASTA.App.Services
{
    public class MessageBoxPromptService : IUserPromptService
    {
        public Task<bool> AskYesNoAsync(string message, string title)
        {
            // Ensure dialog runs on UI thread
            var result = MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return Task.FromResult(result == MessageBoxResult.Yes);
        }

        public Task AskOkAsync(string message, string title)
        {
            // Ensure dialog runs on UI thread
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return Task.CompletedTask;
        }

        public Task<ColdSkyCandidate?> PickColdSkyLocationAsync(
            IReadOnlyList<ColdSkyCandidate> candidates,
            Func<IReadOnlyList<ColdSkyCandidate>, IReadOnlyList<ColdSkyCandidate>> recalculate)
        {
            var window = new ColdSkySelectionWindow(candidates, recalculate)
            {
                Owner = Application.Current?.MainWindow
            };

            bool? result = window.ShowDialog();

            return Task.FromResult(result == true ? window.SelectedCandidate : null);
        }
    }
}
