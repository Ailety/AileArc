using System.IO;
using AileArc.Core;

namespace AileArc.Client;

public sealed partial class ClientApp
{
    internal string SelectedLanguage { get; private set; } = Text.Language;
    private string savedLanguage = Text.Language;
    internal bool SavingLanguage { get; private set; }
    internal bool LanguageSaveFailed { get; private set; }
    internal bool RestartDeferred { get; private set; }
    internal Task LanguageSettled { get; private set; } = Task.CompletedTask;
    internal Func<AppSettings, Task> WriteSettings { get; set; } = settings => Task.Run(settings.SaveAsync);

    internal void SelectLanguage(string language)
    {
        Dispatcher.VerifyAccess();
        if (Restarting || SelectedLanguage == language || !LanguageService.Supported.Contains(language)) return;
        SelectedLanguage = language; LanguageSaveFailed = RestartDeferred = false;
        if (SavingLanguage) { ShowLanguageChoice(); return; }
        SavingLanguage = true;
        ShowLanguageChoice();
        LanguageSettled = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                // Keep only the newest selection queued behind the current disk write.
                while (savedLanguage != SelectedLanguage)
                {
                    string next = SelectedLanguage;
                    try { await WriteSettings(new(next)); savedLanguage = next; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        if (next != SelectedLanguage) continue;
                        SelectedLanguage = savedLanguage; LanguageSaveFailed = true; break;
                    }
                }
            }
            finally
            {
                SavingLanguage = false;
                ShowLanguageChoice(); ExitIfIdle();
            }
        }
    }

    internal void DeferRestart()
    {
        if (SavingLanguage || savedLanguage == Text.Language) return;
        RestartDeferred = true; ShowLanguageChoice();
    }
    private void ShowLanguageChoice()
    {
        foreach (var window in Windows.OfType<MainWindow>()) window.ShowLanguageChoice();
    }
}
