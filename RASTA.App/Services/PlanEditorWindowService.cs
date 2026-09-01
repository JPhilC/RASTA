using System.Windows;
using RASTA.App.Views;

namespace RASTA.App.Services
{
    /// <summary>See IPlanEditorWindowService.</summary>
    public class PlanEditorWindowService : IPlanEditorWindowService
    {
        private PlanEditorWindow? _window;

        public void ShowOrActivate(object planViewModel)
        {
            if (_window is not null)
            {
                _window.Activate();
                return;
            }

            _window = new PlanEditorWindow
            {
                DataContext = planViewModel,
                Owner = Application.Current?.MainWindow
            };
            _window.Closed += (_, __) => _window = null;
            _window.Show();
        }
    }
}
