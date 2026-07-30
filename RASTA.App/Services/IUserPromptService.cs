namespace RASTA.App.Services
{
    public interface IUserPromptService
    {
        Task<bool> AskYesNoAsync(string message, string title);
    }
}
