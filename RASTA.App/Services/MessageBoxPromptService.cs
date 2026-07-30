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
    }
}
