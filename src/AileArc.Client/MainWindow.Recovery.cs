using System.Windows;
using System.Windows.Controls;
using AileArc.Core.Recovery;
using AileArc.Core.WorkCopies;

namespace AileArc.Client;

public partial class MainWindow
{
    internal RecoveryState? LastCleanup { get; private set; }
    internal void ForgetCopy(string id) { openedCopies.Remove(id); CopyNotice.Text = ""; }
    private async void Recovery_Click(object sender, RoutedEventArgs e) => await ShowRecoveryAsync();
    internal async Task ShowRecoveryAsync(int pageSize = 200)
    {
        if (IsBusy || closed) return;
        var dialog = new RecoveryDialog(this, ((ClientApp)Application.Current).Recovery, pageSize);
        try { dialog.ShowDialog(); }
        finally { await dialog.FinishAsync(); }
    }

    internal bool ConfirmRecovery(Window owner, RecoveryItem item)
    {
        string prompt = Text[item.State == RecoveryState.Missing ? "ClientRemoveRecordHint" : "RecoveryConfirm"];
        return Confirm("Recovery", prompt + Environment.NewLine + item.Record.Path, "Clean", owner: owner);
    }

    internal async Task CleanupRecoveryAsync(RecoveryItem reviewed, CancellationToken dialogToken = default)
    {
        var app = (ClientApp)Application.Current;
        LastCleanup = null;
        if (!app.TryBeginCleanup(this)) { StatusText.Text = Text["CleanupBusy"]; return; }
        try
        {
            await RunAsync(Text["Recovery"], async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, dialogToken);
                var result = await Task.Run(() => app.Recovery.Cleanup(reviewed, linked.Token), linked.Token);
                return () =>
                {
                    LastCleanup = result;
                    StatusText.Text = result switch
                    {
                        RecoveryState.Recoverable => Text["RecoveryCleaned"],
                        RecoveryState.Missing => Text["ClientRecordRemoved"],
                        RecoveryState.NoRecord => Text["ClientRecordAlreadyGone"],
                        _ => Text["RecoveryCleanupRefused"]
                    };
                };
            }, false);
        }
        finally { app.EndCleanup(); }
    }
    internal async Task CleanupCopyAsync(WorkCopyRecord record)
    {
        var app = (ClientApp)Application.Current;
        if (!app.TryBeginCleanup(this)) { StatusText.Text = Text["CleanupBusy"]; return; }
        try
        {
            await RunAsync(Text["Clean"], async token =>
            {
                await Task.Run(() => Copies.RemoveUnchangedAsync(record, token), token);
                return () => { app.ForgetCopy(record.Id); StatusText.Text = Text["CopyCleaned"]; };
            }, false);
        }
        finally { app.EndCleanup(); }
    }
}
