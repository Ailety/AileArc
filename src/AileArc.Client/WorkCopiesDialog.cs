using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AileArc.Core;
using AileArc.Core.WorkCopies;

namespace AileArc.Client;

internal enum CopyAction { Export, Open, Clean }

internal sealed class WorkCopiesDialog : ActionDialog
{
    private readonly WorkCopyStore store;
    private readonly List<WorkCopyRecord> records;
    private readonly ListBox entries = new() { Height = 156, Name = "CopyList" };
    private readonly TextBox search = new() { Name = "CopySearch" };
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 88, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) };
    private readonly Button reopen = new() { Name = "ReopenCopy" }, clean = new(), folder = new();
    private CancellationTokenSource? checking;
    private static LanguageService Text => ClientApp.Text;
    internal WorkCopyRecord? Selected => entries.SelectedItem as WorkCopyRecord;
    internal CopyAction Choice { get; private set; } = CopyAction.Export;
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    internal WorkCopiesDialog(MainWindow owner, WorkCopyStore store, WorkCopyListing listing, Func<Task<WorkCopyListing>> readPage, Action<string> openFolder)
        : base(owner, "WorkCopies", "SaveCopyAs", "Close")
    {
        this.store = store; records = listing.Records.ToList();
        Width = 650;
        Explain(Text["CopiesRetainedHint"]);
        var label = new Label { Content = Text["ClientCopySearch"], Target = search, Foreground = Foreground };
        AutomationProperties.SetName(search, Text["ClientCopySearch"]);
        Body.Children.Add(label); Body.Children.Add(search);
        entries.ItemTemplate = (DataTemplate)owner.FindResource("WorkCopyItem");
        entries.ItemContainerStyle = (Style)owner.FindResource("WorkCopyRow");
        entries.Background = (System.Windows.Media.Brush)FindResource("CanvasBrush"); entries.Foreground = Foreground;
        entries.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetName(entries, Text["WorkCopies"]);
        Body.Children.Add(entries); Body.Children.Add(state); Body.Children.Add(details);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        reopen.Content = Text["ClientDefaultOpen"]; clean.Content = Text["CleanUnchangedCopy"]; folder.Content = Text["OpenCopyFolder"];
        foreach (var button in new[] { reopen, folder, clean }) actions.Children.Add(button);
        Body.Children.Add(actions);
        if (listing.HasMore)
        {
            var more = new Button { Content = Text["ClientMoreCopies"], Name = "MoreCopies" };
            async Task LoadMoreAsync()
            {
                more.IsEnabled = false;
                try
                {
                    var page = await readPage();
                    if (!IsVisible) return;
                    records.AddRange(page.Records); records.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
                    Filter(); more.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
                }
                catch (OperationCanceledException) when (!IsVisible) { }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArchiveOperationException or System.ComponentModel.Win32Exception)
                { state.Text = Text["CopyUnavailable"]; }
                finally { more.IsEnabled = true; }
            }
            more.Click += (_, _) => PendingLoad = LoadMoreAsync();
            Body.Children.Add(more);
        }
        reopen.Click += (_, _) => Choose(CopyAction.Open);
        clean.Click += (_, _) => Choose(CopyAction.Clean);
        entries.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && reopen.IsEnabled) { e.Handled = true; Choose(CopyAction.Open); }
        };
        entries.MouseDoubleClick += (_, e) =>
        {
            if (reopen.IsEnabled && e.OriginalSource is DependencyObject child &&
                ItemsControl.ContainerFromElement(entries, child) is ListBoxItem { IsSelected: true })
            { e.Handled = true; Choose(CopyAction.Open); }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { e.Handled = true; search.Focus(); search.SelectAll(); }
        };
        folder.Click += (_, _) =>
        {
            if (Selected is not { } record) return;
            try { openFolder(store.FindCopyFolder(record)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArchiveOperationException)
            { state.Text = Text["CopyUnavailable"]; }
        };
        entries.SelectionChanged += async (_, _) => await InspectAsync();
        search.TextChanged += (_, _) => Filter();
        Closed += (_, _) => checking?.Cancel();
        Loaded += (_, _) => { Filter(); search.Focus(); };
    }

    private void Choose(CopyAction action) { Choice = action; DialogResult = true; }

    private void Filter()
    {
        string term = search.Text.Trim();
        var previous = Selected;
        var matches = records.Where(r => r.ArchivePath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            r.EntryPath.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        entries.ItemsSource = matches;
        if (previous is not null && matches.Contains(previous)) entries.SelectedItem = previous;
        else if (matches.Length > 0) entries.SelectedIndex = 0;
        else
        {
            Accept.IsEnabled = reopen.IsEnabled = clean.IsEnabled = folder.IsEnabled = false;
            state.Text = Text["NoResults"]; details.Clear();
        }
    }

    private async Task InspectAsync()
    {
        checking?.Cancel();
        Accept.IsEnabled = reopen.IsEnabled = clean.IsEnabled = false;
        folder.IsEnabled = Selected is not null;
        if (Selected is not { } record) return;
        using var request = new CancellationTokenSource(); checking = request;
        state.Text = Text["CheckingCopy"];
        details.Text = Text.Format("ClientCopyOrigin", record.ArchivePath, record.EntryPath, record.CreatedUtc.ToLocalTime(), store.FilePath(record));
        try
        {
            var current = await Task.Run(() => store.InspectAsync(record, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            state.Text = Text["CopyState" + current.State];
            if (record.Damage is { } damage) state.Text = Text.Format("ClientDamagedCopyState", Text[damage.Reason], MainWindow.DamageSize(damage)) +
                Environment.NewLine + state.Text;
            Accept.Content = Text[record.Damage is null ? "SaveCopyAs" : "ClientExportAvailable"];
            Accept.IsEnabled = reopen.IsEnabled = current.State is WorkCopyState.Modified or WorkCopyState.Unchanged;
            clean.IsEnabled = current.State == WorkCopyState.Unchanged && !((ClientApp)Application.Current).AnyTaskRunning;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (ReferenceEquals(checking, request)) checking = null; }
    }
}
