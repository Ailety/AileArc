using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AileArc.Core.WorkCopies;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckRetainedCopiesAsync(ClientApp app, string root)
    {
        var prepare = app.CreateWindow(); prepare.Show();
        prepare.LaunchFile = (_, _) => { };
        prepare.ConfirmOverride = (_, _) => true;
        string edited = "", script = "";
        try
        {
            foreach (string name in new[] { "白天稿", "夜间稿" })
            {
                string source = Path.Combine(root, name + ".zip");
                using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("配置/config.yml").Open())) writer.Write(name);
                    if (name == "夜间稿") zip.CreateEntry("helper.cmd");
                }
                await prepare.TestOpenAsync(source);
                await prepare.OpenExternalAsync(0);
                if (name == "夜间稿")
                {
                    edited = prepare.LastCopyPath!;
                    await File.WriteAllTextAsync(edited, "edited outside the archive");
                    await prepare.OpenExternalAsync(1);
                    script = prepare.LastCopyPath!;
                    File.Move(source, source + ".moved");
                }
            }
            await CheckDamagedOpeningAsync(prepare, app, root);
        }
        finally { prepare.ApproveClose(); prepare.Close(); }
        // A new store and an empty window must work without the original archive.
        app.WorkCopies = new WorkCopyStore(app.WorkCopies.Root);
        var window = app.CreateWindow(); window.Show();
        string? launched = null;
        window.LaunchFile = (file, _) => launched = file;
        try
        {
            async Task UseManagerAsync(string filter, bool morePages = false, string archiveName = "夜间稿.zip", string? entryName = null)
            {
                var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += async (_, _) =>
                {
                    if (app.Windows.OfType<WorkCopiesDialog>().FirstOrDefault() is not { IsLoaded: true } dialog) return;
                    timer.Stop();
                    try
                    {
                        var search = (TextBox)LogicalTreeHelper.FindLogicalNode(dialog, "CopySearch");
                        var list = (ListBox)LogicalTreeHelper.FindLogicalNode(dialog, "CopyList");
                        var open = (Button)LogicalTreeHelper.FindLogicalNode(dialog, "ReopenCopy");
                        search.Text = "not-a-working-copy";
                        Require(list.Items.Count == 0 && !open.IsEnabled && !dialog.Accept.IsEnabled, "no result cannot reopen a stale selection");
                        search.Text = filter;
                        if (morePages)
                        {
                            var more = (Button)LogicalTreeHelper.FindLogicalNode(dialog, "MoreCopies");
                            Require(more?.IsVisible == true, "catalog with another page offers continuation");
                            while (more!.IsVisible)
                            {
                                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                await dialog.PendingLoad.WaitAsync(TimeSpan.FromSeconds(5));
                            }
                            Require(search.Text == filter, "loaded pages preserve the active filter");
                        }
                        var record = list.Items.Cast<WorkCopyRecord>().Single(r => Path.GetFileName(r.ArchivePath) == archiveName &&
                            r.EntryPath.EndsWith(entryName ?? (filter == "helper.cmd" ? "helper.cmd" : "config.yml"), StringComparison.Ordinal));
                        list.SelectedItem = record;
                        var until = DateTime.UtcNow.AddSeconds(5);
                        while (!open.IsEnabled && DateTime.UtcNow < until) await Task.Delay(15);
                        Require(open.IsEnabled && dialog.Accept.IsEnabled, "readable retained copy can reopen and export");
                        if (record.Damage is not null) Require(Equals(dialog.Accept.Content, ClientApp.Text["ClientExportAvailable"]),
                            "retained unverified content is labelled on export");
                        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        inspected.TrySetResult();
                    }
                    catch (Exception e) { dialog.Close(); inspected.TrySetException(e); }
                };
                timer.Start();
                try { await window.ShowCopiesAsync(morePages ? 1 : 2000); await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { timer.Stop(); }
            }
            await UseManagerAsync("夜间稿", morePages: true);
            Require(launched == edited && await File.ReadAllTextAsync(launched!) == "edited outside the archive", "recover edited namesake from its original archive");
            window.ConfirmOverride = (_, _) => false;
            await UseManagerAsync("helper.cmd");
            Require(launched == edited, "declined executable recovery never launches");
            window.ConfirmOverride = (_, _) => true;
            await UseManagerAsync("helper.cmd");
            Require(launched == script, "explicit confirmation launches exact retained executable copy");
            var checks = new List<string>();
            window.ConfirmOverride = (title, _) => { checks.Add(title); return title != "ClientDamagedCopy"; };
            await UseManagerAsync("损坏样本", archiveName: "损坏样本.zip", entryName: "sample.cmd");
            Require(launched == script && checks.SequenceEqual(["ConfirmExecutable", "ClientDamagedCopy"]),
                "restored damaged script requires both approvals and declining the second does not launch");
            window.ConfirmOverride = (_, _) => false;
            Require(!await window.PrepareCloseAsync(), "reopened modified copy participates in close protection");
        }
        finally { window.ApproveClose(); window.Close(); }
    }

    private static async Task CheckDamagedOpeningAsync(MainWindow window, ClientApp app, string root)
    {
        string archive = Path.Combine(root, "损坏样本.zip");
        byte[] note = "readable but corrupt"u8.ToArray(), script = "harmless script fixture"u8.ToArray();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("notes.txt", CompressionLevel.NoCompression).Open()) stream.Write(note);
            using (var stream = zip.CreateEntry("sample.cmd", CompressionLevel.NoCompression).Open()) stream.Write(script);
        }
        byte[] damaged = await File.ReadAllBytesAsync(archive);
        foreach (var bytes in new[] { note, script })
        {
            int index = damaged.AsSpan().IndexOf(bytes);
            Require(index >= 0, "fixture contains the stored entry");
            damaged[index] ^= 1; bytes[0] ^= 1;
        }
        await File.WriteAllBytesAsync(archive, damaged);
        await window.TestOpenAsync(archive);
        window.ConfirmOverride = null;
        int launches = 0;
        window.LaunchFile = (path, _) => { Require(File.ReadAllBytes(path).SequenceEqual(note), "only decoded fixture bytes reach the default app"); launches++; };
        async Task Decide(bool accept) => await ObserveDialogAsync(app, ClientApp.Text["ClientDamagedCopy"], () => window.OpenExternalAsync(0), dialog =>
        {
            var hint = dialog.Body.Children.OfType<TextBlock>().Single().Text;
            Require(hint.Contains("notes.txt") && hint.Contains(ClientApp.Text["IntegrityFailed"]) &&
                hint.Contains(ClientApp.Text.Format("ClientDecodedOf", note.Length, note.Length)), "integrity decision identifies the entry, error and byte count");
        }, accept);
        await Decide(false);
        Require(launches == 0 && window.StatusText.Text == ClientApp.Text["ClientUnverifiedKept"], "keeping damaged data does not launch an application");
        await Decide(true);
        Require(launches == 1 && window.StatusText.Text == ClientApp.Text["ClientUnverifiedOpened"], "cached unverified copy still requires explicit approval");
        string copyPath = window.LastCopyPath!;
        window.ConfirmOverride = (_, _) => { window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true; };
        await window.OpenExternalAsync(0);
        Require(launches == 1 && window.StatusText.Text == ClientApp.Text["Cancelled"], "cancellation wins over an outstanding open confirmation");
        window.ConfirmOverride = (_, _) => { File.WriteAllText(copyPath, "changed during review"); return true; };
        await window.OpenExternalAsync(0);
        Require(launches == 1 && window.StatusText.Text == ClientApp.Text["CopyChangedDuringConfirmation"], "approval does not launch substituted bytes");
        window.ConfirmOverride = (title, _) => title == "ConfirmExecutable";
        await window.OpenExternalAsync(1);
        Require(launches == 1 && window.StatusText.Text == ClientApp.Text["ClientUnverifiedKept"], "preparing a damaged executable does not run it");
        Require((await File.ReadAllBytesAsync(archive)).SequenceEqual(damaged), "private recovery preserves the original archive");
        File.Move(archive, archive + ".moved");
    }
}
