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
        }
        finally { prepare.ApproveClose(); prepare.Close(); }
        // A new store and an empty window must work without the original archive.
        app.WorkCopies = new WorkCopyStore(app.WorkCopies.Root);
        var window = app.CreateWindow(); window.Show();
        string? launched = null;
        window.LaunchFile = (file, _) => launched = file;
        try
        {
            async Task UseManagerAsync(string filter, bool morePages = false)
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
                        list.SelectedItem = list.Items.Cast<WorkCopyRecord>().Single(r => r.ArchivePath.EndsWith("夜间稿.zip", StringComparison.Ordinal) &&
                            (filter == "helper.cmd" || r.EntryPath.EndsWith("config.yml", StringComparison.Ordinal)));
                        var until = DateTime.UtcNow.AddSeconds(5);
                        while (!open.IsEnabled && DateTime.UtcNow < until) await Task.Delay(15);
                        Require(open.IsEnabled && dialog.Accept.IsEnabled, "readable retained copy can reopen and export");
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
            window.ConfirmOverride = (_, _) => false;
            Require(!await window.PrepareCloseAsync(), "reopened modified copy participates in close protection");
        }
        finally { window.ApproveClose(); window.Close(); }
    }
}
