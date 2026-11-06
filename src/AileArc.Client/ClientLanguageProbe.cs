using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AileArc.Core;

namespace AileArc.Client;

// Paired processes exercise the real restart command, wait, single-instance pipe
// and session restore with isolated preferences and an isolated broker key.
internal static class ClientLanguageProbe
{
    internal static string[] CreateSession(string root)
    {
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("The restart probe needs an empty test directory.");
        string path = Path.Combine(root, "语言样本.zip");
        using (var file = File.Open(path, FileMode.CreateNew))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) zip.CreateEntry("说明.txt");
        ClientApp.StartupArchive[] session = [new(path, 936), new("", Empty: true), new(path, 65001)];
        return ["--restore-session", JsonSerializer.Serialize(session)];
    }

    internal static async Task CheckAsync(ClientApp app, string root, string mode)
    {
        var windows = app.Windows.OfType<MainWindow>().ToArray();
        string path = Path.Combine(root, "语言样本.zip");
        if (windows.Length != 3 || windows.Count(w => w.OpenedPath is null) != 1 ||
            !windows.Where(w => w.OpenedPath == path && w.TestGrid.Items.Count == 1).Select(w => w.CurrentCodePage).Order().SequenceEqual([936, 65001]))
            throw new InvalidOperationException("Session restore lost a duplicate window, entry, encoding or empty window.");
        string expected = mode == "language-restored" ? "en-US" : "zh-CN";
        var text = new LanguageService(expected);
        if (ClientApp.Text.Language != expected || windows.Any(w => !Equals(w.RecoveryButton.Content, text["Recovery"])))
            throw new InvalidOperationException("Restored windows do not use the expected session language.");
        if (windows.Any(w => w.RestartButton.Visibility != Visibility.Collapsed)) throw new InvalidOperationException("Stale restart prompt.");
        if (mode != "language-restart")
        {
            string name = mode == "language-returned" ? "restored" : "english";
            var report = new { pid = Environment.ProcessId, language = ClientApp.Text.Language, windows = windows.Length,
                codePages = windows.Where(w => w.OpenedPath is not null).Select(w => w.CurrentCodePage).Order().ToArray() };
            await File.WriteAllTextAsync(Path.Combine(root, name + ".tmp"), JsonSerializer.Serialize(report));
            File.Move(Path.Combine(root, name + ".tmp"), Path.Combine(root, name + ".json"));
        }
        if (mode != "language-returned")
        {
            int chosen = expected == "zh-CN" ? 1 : 0;
            windows[0].LanguageBox.SelectedIndex = chosen;
            await app.LanguageSettled;
            if (windows.Any(w => w.LanguageBox.SelectedIndex != chosen || w.RestartButton.Visibility != Visibility.Visible ||
                !Equals(w.RecoveryButton.Content, text["Recovery"]))) throw new InvalidOperationException("Pending language was not shared.");
            windows[1].RestartLater.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!app.RestartDeferred || windows.Any(w => w.RestartLater.Visibility != Visibility.Collapsed))
                throw new InvalidOperationException("Deferral was not shared.");
            if (await app.TryRestartAsync() != ClientApp.RestartResult.Started) throw new InvalidOperationException("Restart was rejected.");
            return;
        }
        foreach (var window in windows) window.ApproveClose();
        app.Shutdown();
    }
}
