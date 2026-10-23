using System.Windows;
using System.Windows.Controls;
using AileArc.Core.Recovery;
using AileArc.Core.WorkCopies;

namespace AileArc.Client;

public partial class MainWindow
{
    internal RecoveryState? LastCleanup { get; private set; }
    internal void ForgetCopy(string id) { openedCopies.Remove(id); CopyNotice.Text = ""; }
    private async void Recovery_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<RecoveryItem> items = [];
        bool loaded = false;
        await RunAsync(Text["Recovery"], async token =>
        { items = await Task.Run(() => ((ClientApp)Application.Current).Recovery.List(), token); return () => loaded = true; });
        if (!loaded || closed) return;
        if (items.Count == 0) { StatusText.Text = Text["NoRecoveryItems"]; return; }
        var dialog = new ActionDialog(this, "Recovery", "Clean", "Close");
        dialog.Explain(Text["RecoveryHint"]);
        var selector = dialog.Choices(Text["Recovery"], items.Select(i => $"{i.Record.CreatedUtc.ToLocalTime():g} · {Text["RecoveryKind" + i.Record.Kind]}").ToArray());
        var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 150 }; dialog.Body.Children.Add(detail);
        void Refresh()
        {
            var item = items[selector.SelectedIndex]; detail.Text = $"{Text["RecoveryState" + item.State]}\n{item.Record.Path}\n{item.Bytes:N0} B";
            dialog.Accept.IsEnabled = item.State is RecoveryState.Recoverable or RecoveryState.Missing;
        }
        selector.SelectionChanged += (_, _) => Refresh(); Refresh();
        if (dialog.ShowDialog() != true) return;
        var record = items[selector.SelectedIndex].Record;
        if (Confirm("Recovery", Text["RecoveryConfirm"] + Environment.NewLine + record.Path, "Clean")) await CleanupRecoveryAsync(record.Id);
    }
    internal async Task CleanupRecoveryAsync(string id)
    {
        var app = (ClientApp)Application.Current;
        LastCleanup = null;
        if (!app.TryBeginCleanup(this)) { StatusText.Text = Text["CleanupBusy"]; return; }
        try
        {
            await RunAsync(Text["Recovery"], async token =>
            {
                var result = await Task.Run(() => app.Recovery.Cleanup(id, token), token);
                return () => { LastCleanup = result; StatusText.Text = Text[result is RecoveryState.Recoverable or RecoveryState.Missing ? "RecoveryCleaned" : "RecoveryCleanupRefused"]; };
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
