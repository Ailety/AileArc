using System.IO;
using System.IO.Compression;
using System.Windows.Threading;
using AileArc.Core;
using AileArc.DesktopBridge;
using AileArc.Core.Extraction;
using AileArc.Core.WorkCopies;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    public static int Run(ClientApp app)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "client-self-test-error.txt"), "", System.Text.Encoding.UTF8);
        int result = 1;
        string testRoot = Path.Combine(Path.GetTempPath(), "ailearc-p2-" + Guid.NewGuid().ToString("N"));
        app.WorkCopies = new WorkCopyStore(Path.Combine(testRoot, "copies"));
        app.HideTestWindows = true;
        var window = new MainWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        window.ContentRendered += async (_, _) =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "ailearc-client-test-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                var zh = LanguageService.Load("zh-CN"); var en = LanguageService.Load("en-US");
                foreach (string key in zh.Keys.Where(k => k.StartsWith("Client", StringComparison.Ordinal))) Require(en.ContainsKey(key), "missing translation");
                Require(window.Title == ClientApp.Text["ClientTitle"], "localized window title");
                Require(window.TestGrid.Items.Count == 0, "formal client starts without generated data");
                var restored = ClientApp.ParseArguments(["--restore-session", "[{\"Path\":\"C:/测试.zip\",\"CodePage\":936}]"]);
                Require(restored.Length == 1 && restored[0].CodePage == 936 && restored[0].Path == "C:/测试.zip", "restart preserves filename encoding");
                bool refused = false;
                try { ClientApp.ParseArguments(["--restore-session", "[{\"Path\":\"C:/test.zip\",\"CodePage\":999}]"]); }
                catch (ArgumentException) { refused = true; }
                Require(refused, "unsupported encoding rejected");
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                { using var writer = new StreamWriter(zip.CreateEntry("文档/说明.txt").Open(), System.Text.Encoding.UTF8); writer.Write("public test fixture"); }
                await window.TestOpenAsync(temp);
                Require(window.TestHasArchive && window.TestGrid.Items.Count == 1, "real worker and implicit directory");
                var rows = Enumerable.Range(0, 100000).Select(i => new EntryView(new BrowserRow(i.ToString(), $"测试_{i}.md", "", $"测试_{i}.md", "md", 1, "—", false))).ToArray();
                window.ApplyRows(rows); window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                window.TestGrid.SelectedItems.Add(rows[1]); window.TestGrid.SelectedItems.Add(rows[3]);
                Require(window.TestAllState is null && window.TestGrid.SelectedItems.Count == 2, "mixed selection");
                window.ToggleAll(); Require(window.TestGrid.SelectedItems.Count == 100000 && window.TestAllState is true, "select all");
                window.TestGrid.UnselectAll(); window.TestGrid.ScrollIntoView(rows[^1]);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Require(window.TestGrid.ItemContainerGenerator.ContainerFromIndex(99999) is not null, "last virtual row");
                await window.TestOpenAsync(temp);
                Require(window.TestHasArchive && window.TestGrid.Items.Count == 1, "real worker and implicit directory");
                Require(window.TestGrid.SelectedItems.Count == 0, "session replacement clears selection");
                await window.TestOpenAsync(temp + ".missing");
                Require(window.OpenedPath == temp && window.TestGrid.Items.Count == 1, "failed open retains previous session");
                await CheckOperationsAsync(window, app, temp, testRoot);
                await CheckP3Async(window, app, testRoot);
                await CheckShellAsync(window, app, testRoot);
                var empty = new MainWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
                empty.Show(); empty.Close();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(!empty.IsVisible, "idle window closes without Closing event reentrancy");
                result = 0;
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "client-self-test-error.txt"), error.ToString(), System.Text.Encoding.UTF8); }
            finally { if (File.Exists(temp)) File.Delete(temp); window.ApproveClose(); window.Close(); app.Shutdown(result); }
        };
        app.Run(window); return result;
    }
    private static async Task CheckOperationsAsync(MainWindow window, ClientApp app, string archive, string root)
    {
        byte[] original = await File.ReadAllBytesAsync(archive);
        string extracted = Path.Combine(root, "selected");
        window.TestGrid.SelectAll();
        await window.ExtractToAsync(extracted, false, true);
        Require(window.LastExtraction is { Completed: 1, Cancelled: false } && File.Exists(Path.Combine(extracted, "文档", "说明.txt")), "selected implicit folder expands to real entry IDs");
        window.ConflictOverride = (_, _) => Task.FromResult(new ConflictDecision(ConflictChoice.Skip));
        await window.ExtractToAsync(extracted, false, false);
        Require(window.LastExtraction is { Skipped: 1, Completed: 0 }, "skip conflict");
        window.ConflictOverride = (_, _) => Task.FromResult(new ConflictDecision(ConflictChoice.Rename));
        await window.ExtractToAsync(extracted, false, false);
        Require(window.LastExtraction is { Completed: 1 } && Directory.GetFiles(Path.Combine(extracted, "文档")).Length == 2, "rename collision");
        window.ConflictOverride = (_, _) => Task.FromResult(new ConflictDecision(ConflictChoice.Cancel));
        await window.ExtractToAsync(extracted, false, false);
        Require(window.LastExtraction is { Cancelled: true } && File.ReadAllText(Path.Combine(extracted, "文档", "说明.txt")) == "public test fixture", "cancel retains existing content");
        window.ConflictOverride = (_, _) => Task.FromResult(new ConflictDecision(ConflictChoice.Replace));
        await File.WriteAllTextAsync(Path.Combine(extracted, "文档", "说明.txt"), "old target");
        await window.ExtractToAsync(extracted, false, false);
        Require(File.ReadAllText(Path.Combine(extracted, "文档", "说明.txt")) == "public test fixture", "replace conflict");
        var atConflict = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ConflictOverride = async (_, token) => { atConflict.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(ConflictChoice.Cancel); };
        var running = window.ExtractToAsync(extracted, false, false);
        await atConflict.Task.WaitAsync(TimeSpan.FromSeconds(15));
        window.ConfirmOverride = (_, _) => false;
        Require(!await window.PrepareCloseAsync() && window.IsBusy, "keep waiting preserves active task");
        window.ConfirmOverride = (_, _) => true;
        Require(await window.PrepareCloseAsync() && !window.IsBusy, "cancel-and-close awaits worker cleanup");
        await running;
        Require(window.LastExtraction is { Cancelled: true }, "active cancellation result retained");
        window.ConflictOverride = null;
        await window.ExtractToAsync(Path.Combine(root, "smart"), true, false);
        Require(File.Exists(Path.Combine(root, "smart", "文档", "说明.txt")), "smart extraction preserves sole root folder");
        int launches = 0;
        window.LaunchFile = (file, _) => { Require(File.Exists(file), "launch only after extraction"); launches++; };
        await window.OpenExternalAsync(0);
        Require(launches == 1 && window.LastCopyPath is not null, "default app receives prepared copy");
        string copy = window.LastCopyPath!;
        await File.WriteAllTextAsync(copy, "edited externally");
        await window.OpenExternalAsync(0);
        Require(launches == 2 && window.LastCopyPath == copy && File.ReadAllText(copy) == "edited externally", "reopen reuses modified copy");
        var record = (await app.WorkCopies.LoadAsync()).Single(r => app.WorkCopies.FilePath(r) == copy);
        Require((await app.WorkCopies.InspectAsync(record)).State == WorkCopyState.Modified, "detect modification");
        await window.ExportCopyAsync(record, Path.Combine(root, "saved.txt"));
        Require(File.ReadAllText(Path.Combine(root, "saved.txt")) == "edited externally", "export edited copy");
        await window.ExportCopyAsync(record, archive);
        byte[] afterExport = await File.ReadAllBytesAsync(archive);
        Require(original.SequenceEqual(afterExport), "export refuses original archive");
        window.ConfirmOverride = (_, _) => false;
        Require(!await window.PrepareCloseAsync() && File.Exists(copy), "declined close retains copy");
        window.ConfirmOverride = (_, _) => true;
        Require(await window.PrepareCloseAsync() && File.Exists(copy), "accepted close still retains copy");
        // An externally replaced source must not let old entry IDs write or launch anything.
        await File.AppendAllTextAsync(archive, "changed");
        await window.ExtractToAsync(Path.Combine(root, "changed"), false, false);
        Require(window.LastExtraction is null && !Directory.Exists(Path.Combine(root, "changed")), "source identity change refuses extraction");
        Require(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(), "no incomplete output after cancellation");
        string executableArchive = Path.Combine(root, "executable.zip");
        using (var zip = ZipFile.Open(executableArchive, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("never-run.cmd").Open()); writer.Write("public fixture; never execute"); }
        await window.TestOpenAsync(executableArchive);
        bool asked = false;
        window.ConfirmOverride = (title, _) => { asked = title == "ConfirmExecutable"; return false; };
        await window.OpenExternalAsync(0);
        Require(asked && launches == 2, "executable refusal never launches");
        // Generated test files are left under this unique temp root for failure diagnosis; never touch user copies.
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
