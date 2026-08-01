namespace RASTA.App.Services
{
    public interface IUserPromptService
    {
        Task<bool> AskYesNoAsync(string message, string title);
        Task AskOkAsync(string message, string title);
    }
}
