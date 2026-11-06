using System.IO;
using System.Windows;
using System.Windows.Controls;
using AileArc.Core;
using AileArc.Core.Extraction;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckLanguageChoicesAsync(ClientApp app, string root)
    {
        string path = Path.Combine(root, "language-choices", "settings.json");
        string current = ClientApp.Text.Language, other = current == "zh-CN" ? "en-US" : "zh-CN";
        int activeIndex = current == "zh-CN" ? 0 : 1;
        await new AppSettings(current).SaveAsync(path);
        var originalWriter = app.WriteSettings;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        app.WriteSettings = async choice =>
        {
            calls.Add(choice.Language);
            await release.Task;
            await choice.SaveAsync(path);
        };
        var first = app.CreateWindow(); var second = app.CreateWindow(); first.Show(); second.Show();
        MainWindow? third = null;
        try
        {
            first.LanguageBox.SelectedIndex = 1 - activeIndex;
            Require(app.SavingLanguage && second.LanguageBox.SelectedIndex == 1 - activeIndex, "pending language is shared before writing completes");
            second.LanguageBox.SelectedIndex = activeIndex;
            Require(first.LanguageBox.SelectedIndex == activeIndex && calls.SequenceEqual([other]) &&
                await app.TryRestartAsync() == ClientApp.RestartResult.Busy, "late request waits behind the current write and prevents restart");
            release.TrySetResult(); await app.LanguageSettled;
            Require(calls.SequenceEqual([other, current]) && (await AppSettings.LoadAsync(path)).Language == current &&
                first.RestartButton.Visibility == Visibility.Collapsed && second.RestartButton.Visibility == Visibility.Collapsed,
                "last selection wins on disk and returning to the session language removes the prompt");

            first.LanguageBox.SelectedIndex = 1 - activeIndex; await app.LanguageSettled;
            third = app.CreateWindow(); third.Show();
            Require(third.LanguageBox.SelectedIndex == 1 - activeIndex && third.RestartButton.Visibility == Visibility.Visible &&
                Equals(third.RecoveryButton.Content, ClientApp.Text["Recovery"]), "new window inherits saved choice but renders the current session language");
            third.RestartLater.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(first.RestartLater.Visibility == Visibility.Collapsed && second.LanguageNotice.Text == ClientApp.Text["ClientLanguageDeferred"],
                "later is shared without reverting the saved language");
            var finishFailedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.WriteSettings = async choice => { await finishFailedWrite.Task; await choice.SaveAsync(path); };
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                second.LanguageBox.SelectedIndex = activeIndex;
                var closing = third.PrepareCloseAsync();
                bool waitingForWrite = !closing.IsCompleted;
                finishFailedWrite.TrySetResult();
                Require(waitingForWrite && !await closing, "a failed pending save leaves the closing window available for retry");
            }
            Require(app.LanguageSaveFailed && new[] { first, second, third }.All(w => w.LanguageBox.SelectedIndex == 1 - activeIndex &&
                w.LanguageNotice.Text == ClientApp.Text["SettingsFailed"]) && (await AppSettings.LoadAsync(path)).Language == other,
                "write failure restores the last durable choice in every window");
            app.WriteSettings = choice => choice.SaveAsync(path);

            string archive = Path.Combine(root, "created.zip"), output = Directory.CreateDirectory(Path.Combine(root, "language-conflict")).FullName;
            await first.TestOpenAsync(archive);
            var entry = first.TestGrid.Items.Cast<EntryView>().Single();
            await File.WriteAllTextAsync(Path.Combine(output, entry.Name), "keep");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.ConflictOverride = async (_, ct) =>
            { entered.TrySetResult(); await resume.Task.WaitAsync(ct); return new(ConflictChoice.Skip); };
            var task = first.ExtractToAsync(output, false, false);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                second.LanguageBox.SelectedIndex = activeIndex; await app.LanguageSettled;
                second.LanguageBox.SelectedIndex = 1 - activeIndex; await app.LanguageSettled;
                second.RestartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(first.IsBusy && !app.Restarting && second.StatusText.Text == ClientApp.Text["ClientRestartBusy"],
                    "restart from a different window explains the active task and leaves it running");
            }
            finally { resume.TrySetResult(); await task; }
            Require(first.LastExtraction is { Cancelled: false, Skipped: 1 }, "deferred restart did not cancel extraction");
        }
        finally
        {
            release.TrySetResult(); await app.LanguageSettled;
            app.WriteSettings = choice => choice.SaveAsync(path); app.SelectLanguage(current); await app.LanguageSettled;
            app.WriteSettings = originalWriter;
            foreach (var w in new[] { first, second, third }) { if (w is null) continue; w.ApproveClose(); w.Close(); }
        }
    }
}
