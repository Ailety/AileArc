using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AileArc.Core;
using AileArc.Core.Recovery;

namespace AileArc.Client;

internal sealed class RecoveryDialog : ActionDialog
{
    private sealed record Row(RecoveryItem Item, string Title, string Summary, string? IntendedPath);
    private static LanguageService Text => ClientApp.Text;
    private readonly MainWindow host;
    private readonly RecoveryStore store;
    private readonly int pageSize;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Row> rows = new(StringComparer.Ordinal);
    private readonly TextBox search = new() { Name = "RecoverySearch" };
    private readonly ListBox list = new() { Name = "RecoveryList", Height = 156 };
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button more = new() { Name = "MoreRecovery", Content = Text["ClientMoreRecords"] };
    private readonly Button refresh = new() { Name = "RefreshRecovery", Content = Text["ClientRefreshRecords"] };
    private readonly Button clean = new() { Name = "CleanRecovery" };
    private readonly Button copy = new() { Name = "CopyRecoveryPath", Content = Text["ClientCopyRecoveryPath"] };
    private IEnumerator<RecoveryPage>? cursor;
    private bool hasMore, busy;
    internal Task Pending { get; private set; } = Task.CompletedTask;

    internal RecoveryDialog(MainWindow host, RecoveryStore store, int pageSize) : base(host, "Recovery", "Close", "Close")
    {
        this.host = host; this.store = store; this.pageSize = pageSize;
        Width = 670;
        Explain(Text["RecoveryHint"]);
        Body.Children.Add(new Label { Content = Text["ClientRecoverySearch"], Target = search, Foreground = Foreground });
        AutomationProperties.SetName(search, Text["ClientRecoverySearch"]);
        Body.Children.Add(search);
        list.Background = (System.Windows.Media.Brush)FindResource("CanvasBrush"); list.Foreground = Foreground;
        list.ItemTemplate = (DataTemplate)host.FindResource("RecoveryRowTemplate");
        list.ItemContainerStyle = (Style)host.FindResource("WorkCopyRow");
        list.Margin = new Thickness(0, 6, 0, 4);
        AutomationProperties.SetName(list, Text["Recovery"]);
        Body.Children.Add(list); Body.Children.Add(details); Body.Children.Add(status);
        var actions = new WrapPanel();
        actions.Children.Add(clean); actions.Children.Add(copy); actions.Children.Add(refresh); actions.Children.Add(more); Body.Children.Add(actions);
        list.SelectionChanged += (_, _) => UpdateActions();
        search.TextChanged += (_, _) => Filter();
        refresh.Click += (_, _) => { if (!busy) Pending = LoadAsync(restart: true); };
        more.Click += (_, _) => { if (!busy && hasMore) Pending = LoadAsync(restart: false); };
        clean.Click += (_, _) => { if (!busy && list.SelectedItem is Row selected) Pending = CleanAsync(selected); };
        copy.Click += (_, _) =>
        {
            if (list.SelectedItem is not Row selected || selected.Item.Record.Path.Length == 0) return;
            try { host.CopyText(selected.Item.Record.Path); status.Text = Text["ClientPathCopied"]; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = Text["ClientClipboardBusy"]; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; search.Focus(); search.SelectAll(); }
            if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && !busy) { e.Handled = true; Pending = LoadAsync(true); }
        };
        Closed += (_, _) => lifetime.Cancel();
        Loaded += (_, _) => { search.Focus(); Pending = LoadAsync(true); };
    }

    private async Task LoadAsync(bool restart)
    {
        busy = true; UpdateActions();
        try
        {
            if (restart)
            {
                cursor?.Dispose();
                cursor = store.ReadPages(pageSize, lifetime.Token).GetEnumerator();
            }
            var page = await Task.Run(() => cursor!.MoveNext() ? cursor.Current : new RecoveryPage([], false), lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (restart) rows.Clear();
            foreach (var item in page.Items) rows[item.Record.Id] = MakeRow(item);
            hasMore = page.HasMore;
            if (!hasMore) { cursor?.Dispose(); cursor = null; }
            Filter();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArchiveOperationException)
        {
            rows.Clear(); hasMore = false; cursor?.Dispose(); cursor = null; Filter(); status.Text = Text["ClientRecoveryReadFailed"];
        }
        finally { busy = false; UpdateActions(); }
    }

    private static Row MakeRow(RecoveryItem item)
    {
        var record = item.Record;
        string kind = record.Kind is "Extract" or "Compress" or "Export" or "Metadata" or "Test" ? Text["RecoveryKind" + record.Kind] : Text["ClientUnknownTask"];
        string title = record.OutputName ?? kind;
        string time = record.CreatedUtc == default ? "" : record.CreatedUtc.ToLocalTime().ToString("g") + " · ";
        string subtitle = record.OutputName is null ? time : time + kind + " · ";
        string? intended = record.OutputName is null ? null : Path.Combine(Path.GetDirectoryName(record.Path)!, record.OutputName);
        return new(item, title, subtitle + Text["RecoveryState" + item.State], intended);
    }

    private void Filter()
    {
        string term = search.Text.Trim();
        string? selected = (list.SelectedItem as Row)?.Item.Record.Id;
        int index = list.SelectedIndex;
        var visible = rows.Values.Where(r => r.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            r.IntendedPath?.Contains(term, StringComparison.OrdinalIgnoreCase) == true || r.Summary.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            r.Item.Record.Path.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            r.Item.Record.Id.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Item.Record.CreatedUtc).ThenBy(r => r.Item.Record.Id).ToArray();
        list.ItemsSource = visible;
        list.SelectedItem = visible.FirstOrDefault(r => r.Item.Record.Id == selected) ??
            (visible.Length == 0 ? null : visible[Math.Clamp(index, 0, visible.Length - 1)]);
        status.Text = visible.Length > 0 ? Text.Format("ClientRecoveryCount", visible.Length, rows.Count) :
            rows.Count == 0 && !hasMore ? Text["NoRecoveryItems"] : Text["NoResults"];
        UpdateActions();
    }

    private void UpdateActions()
    {
        var row = list.SelectedItem as Row;
        var item = row?.Item;
        refresh.IsEnabled = more.IsEnabled = !busy;
        more.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        clean.Content = Text[item?.State == RecoveryState.Missing ? "ClientRemoveRecord" : "Clean"];
        clean.IsEnabled = !busy && item?.State is RecoveryState.Recoverable or RecoveryState.Missing;
        copy.IsEnabled = item?.Record.Path.Length > 0;
        if (item is null) { details.Clear(); return; }
        details.Text = Text["RecoveryState" + item.State] + Environment.NewLine;
        if (row?.IntendedPath is { } intended) details.Text += Text.Format("ClientRecoveryOutput", intended) + Environment.NewLine;
        details.Text += item.Record.Path.Length == 0 ? Text.Format("ClientRecoveryId", item.Record.Id) : Text.Format("ClientRecoveryStaging", item.Record.Path);
        if (item.State == RecoveryState.Recoverable) details.Text += Environment.NewLine + Text.Format("ClientRecoveryBytes", item.Bytes);
    }

    private async Task CleanAsync(Row selected)
    {
        if (selected.Item.State is not (RecoveryState.Recoverable or RecoveryState.Missing)) return;
        busy = true; UpdateActions();
        try
        {
            if (!host.ConfirmRecovery(this, selected.Item)) return;
            await host.CleanupRecoveryAsync(selected.Item, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (host.LastCleanup is RecoveryState.Recoverable or RecoveryState.Missing or RecoveryState.NoRecord) rows.Remove(selected.Item.Record.Id);
            else if (host.LastCleanup is { } refused) rows[selected.Item.Record.Id] = MakeRow(selected.Item with { State = refused, Snapshot = null });
            Filter(); status.Text = host.StatusText.Text;
        }
        finally { busy = false; UpdateActions(); }
    }

    internal async Task FinishAsync()
    {
        lifetime.Cancel();
        try { await Pending; }
        finally { cursor?.Dispose(); lifetime.Dispose(); }
    }
}
