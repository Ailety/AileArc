using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AileArc.Core.Recovery;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckRecoveryManagerAsync(MainWindow window, ClientApp app)
    {
        var interrupted = app.Recovery.List().Single().Record;
        string id = Guid.NewGuid().ToString("N");
        var absent = interrupted with { Id = id, Path = Path.Combine(Path.GetDirectoryName(interrupted.Path)!, $".ailearc-{id}.partial"), OutputName = "已提交.zip" };
        string missingJournal = Path.Combine(app.Recovery.Root, id + ".json");
        await File.WriteAllTextAsync(missingJournal, System.Text.Json.JsonSerializer.Serialize(absent));
        var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        var previousConfirm = window.ConfirmOverride;
        var previousCopy = window.CopyText;
        timer.Tick += async (_, _) =>
        {
            if (app.Windows.OfType<RecoveryDialog>().FirstOrDefault() is not { IsLoaded: true } dialog) return;
            timer.Stop();
            try
            {
                await dialog.Pending;
                var search = (TextBox)LogicalTreeHelper.FindLogicalNode(dialog, "RecoverySearch");
                var more = (Button)LogicalTreeHelper.FindLogicalNode(dialog, "MoreRecovery");
                var clean = (Button)LogicalTreeHelper.FindLogicalNode(dialog, "CleanRecovery");
                var refresh = (Button)LogicalTreeHelper.FindLogicalNode(dialog, "RefreshRecovery");
                var list = (ListBox)LogicalTreeHelper.FindLogicalNode(dialog, "RecoveryList");
                async Task Click(Button button)
                {
                    Require(button.IsEnabled, "recovery action is available");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await dialog.Pending.WaitAsync(TimeSpan.FromSeconds(5));
                }
                search.Text = "no-such-record";
                Require(!clean.IsEnabled && more.IsVisible, "empty filter cannot authorize cleanup; further pages are discoverable");
                await Click(more);
                Require(!more.IsVisible && search.Text == "no-such-record", "next batch preserves active search");
                search.Text = Path.Combine(Path.GetDirectoryName(absent.Path)!, absent.OutputName!);
                Require(list.Items.Count == 1 && Equals(clean.Content, ClientApp.Text["ClientRemoveRecord"]), "missing output offers record-only removal");
                string? copied = null; window.CopyText = text => copied = text;
                var copy = (Button)LogicalTreeHelper.FindLogicalNode(dialog, "CopyRecoveryPath");
                await Click(copy);
                Require(copied == absent.Path, "searching for the intended output copies only the registered staging path");
                window.ConfirmOverride = (_, _) => false;
                await Click(clean);
                Require(dialog.IsVisible && File.Exists(missingJournal), "declining preserves the record and search context");
                window.ConfirmOverride = (_, _) => true;
                await Click(clean);
                Require(window.LastCleanup == RecoveryState.Missing && !File.Exists(missingJournal) && list.Items.Count == 0,
                    "confirmed missing entry only removes its registration");
                search.Text = interrupted.OutputName!;
                window.ConfirmOverride = (_, _) => { File.AppendAllText(interrupted.Path, "changed after inspection"); return true; };
                await Click(clean);
                Require(window.LastCleanup == RecoveryState.Changed && File.Exists(interrupted.Path) && !clean.IsEnabled,
                    "stale confirmation is refused and cannot repeat without refreshing");
                await Click(refresh);
                Require(clean.IsEnabled && list.Items.Count == 1 && search.Text == interrupted.OutputName,
                    "refresh rechecks the selected file and keeps the text filter");
                window.ConfirmOverride = (_, _) => true;
                await Click(clean);
                Require(!clean.IsEnabled && list.Items.Count == 0, "successful cleanup updates the open manager");
                dialog.Close(); inspected.TrySetResult();
            }
            catch (Exception e) { if (dialog.IsVisible) dialog.Close(); inspected.TrySetException(e); }
        };
        timer.Start();
        try { await window.ShowRecoveryAsync(pageSize: 1); await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { timer.Stop(); window.ConfirmOverride = previousConfirm; window.CopyText = previousCopy; }
    }
}
