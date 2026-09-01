namespace RASTA.App.Services
{
    /// <summary>
    /// Opens (or re-activates) the modeless "Edit Plan Details" window from PlanViewModel,
    /// without PlanViewModel needing to reference the concrete RASTA.App.Views.PlanEditorWindow
    /// type directly - same reasoning as IUserPromptService keeping PrepareViewModel decoupled
    /// from ColdSkySelectionWindow.
    /// </summary>
    public interface IPlanEditorWindowService
    {
        /// <summary>
        /// Shows the editor window (DataContext bound to <paramref name="planViewModel"/>) if
        /// none is currently open, or brings the already-open one to the front - never opens a
        /// second instance.
        /// </summary>
        void ShowOrActivate(object planViewModel);
    }
}
