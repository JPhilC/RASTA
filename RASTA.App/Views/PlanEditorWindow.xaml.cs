using System.Windows;

namespace RASTA.App.Views
{
    /// <summary>
    /// The modeless "Edit Plan Details" window - see PlanEditorWindowService, which owns
    /// showing/activating the single instance and binds its DataContext to PlanViewModel.
    /// </summary>
    public partial class PlanEditorWindow : Window
    {
        public PlanEditorWindow()
        {
            InitializeComponent();
        }
    }
}
