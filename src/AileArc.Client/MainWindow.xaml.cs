using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AileArc.Core;
using AileArc.DesktopBridge;

namespace AileArc.Client;

public partial class MainWindow : Window
{
    private readonly ArchiveWorkerClient worker = new(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
    private static readonly int[] CodePages = [0, 65001, 936, 932];
    private readonly string? initialPath;
    private readonly int initialCodePage;
    private ArchivePresentation? archive;
    private EntryView[] displayed = [];
    private CancellationTokenSource? operation;
    private string directory = "", archiveName = "";
    private string? password;
    private int generation, offset, total, codePage;
    private sealed record View(string Directory, string Search, bool Local, int Sort, bool Descending, int Offset);
    private View view = new("", "", false, 0, false, 0);
    private readonly List<(View Page, string[] Selected)> history = [];
    private readonly List<(View Page, string[] Selected)> future = [];
    private static readonly string[] SortKeys = ["name", "size", "type", "path"];
    private readonly Button OpenButton = new();
    private bool composing, closed, changingItems, configuring = true;
    private LanguageService Text => ClientApp.Text;
    public string? OpenedPath { get; private set; }
    internal string? TargetPath => OpenedPath ?? initialPath;
    public bool IsBusy => operation is not null;
    internal int CurrentCodePage => codePage;

    public MainWindow(string? path = null, int initialCodePage = 0)
    {
        initialPath = path;
        this.initialCodePage = initialCodePage;
        InitializeComponent();
        LanguageBox.ItemsSource = new[] { "简体中文", "English (United States)" };
        LanguageBox.SelectedIndex = Text.Language == "en-US" ? 1 : 0;
        OpenButton.Content = Text["Open"]; OpenButton.Click += Open_Click; OpenButton.Style = (Style)FindResource("Primary");
        System.Windows.Automation.AutomationProperties.SetAutomationId(OpenButton,"OpenArchive");
        GraphiteFrame.Install(this, OpenButton);
        PreviewKeyDown += NavigationKey;
        EncodingBox.ItemsSource = new[] { Text["FilenameEncodingAuto"], "UTF-8 (65001)", "GBK (936)", "Shift-JIS (932)" }; EncodingBox.SelectedIndex = 0;
        InitializeSearch();
        InitializeWorkspaceActions();
        EmptyText.Text = Text["ClientDropWelcome"];
        configuring = false; SetControls();
        InitializeOperations();
        Loaded += async (_, _) => { if (initialPath is not null) await OpenAsync(initialPath, this.initialCodePage); };
        Closed += (_, _) => { closed = true; CancelLiveSearch(); generation++; operation?.Cancel(); password = null; ((ClientApp)Application.Current).Unregister(this); };
    }
    private async Task RunAsync(string status, Func<CancellationToken, Task<Action>> prepare, bool resetStatus = true)
    {
        if (IsBusy || closingCheck || !((ClientApp)Application.Current).CanStart(this)) return;
        CancelLiveSearch();
        operation?.Cancel();
        idleCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        int version = ++generation;
        StatusText.Text = status; CancelButton.IsEnabled = true; CancelButton.Visibility = Visibility.Visible; SetControls();
        try
        {
            Action commit = await prepare(cancellation.Token);
            if (closed || version != generation) return;
            if (cancellation.IsCancellationRequested)
            {
                if (StatusText.Text == Text["ClientCancelling"]) StatusText.Text = Text["Cancelled"];
                return;
            }
            commit();
            if (resetStatus) StatusText.Text = archive is null ? Text["ClientIdle"] : Text.Format("ClientStatus", archive.Scan.Format, archive.Index.Entries.Count);
        }
        catch (OperationCanceledException) { if (!closed && version == generation) StatusText.Text = Text["Cancelled"]; }
        catch (InvalidOperationException) when (cancellation.IsCancellationRequested) { }
        catch (ArchiveOperationException error)
        {
            if (!closed && version == generation)
            {
                var message = Text[error.Code == "ArchiveChanged" && archive is not null ? "ClientReloadRequired" : error.Code];
                StatusText.Text = error.AffectedPath is { } affected ? Text.Format("ClientIssueAtPath", message, affected) : message;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { if (!closed && version == generation) StatusText.Text = Text["OpenFailed"]; }
        finally
        {
            if (ReferenceEquals(operation, cancellation))
            {
                operation = null;
                if (!closed) { CancelButton.Visibility = Visibility.Collapsed; SetControls(); }
                idleCompletion.TrySetResult();
            }
        }
    }
    private enum OpenIntent { Open, ChangeEncoding, Refresh }
    private Task OpenAsync(string path, int requestedCodePage = 0, OpenIntent intent = OpenIntent.Open)
    {
        return RunAsync(Text["Scanning"], async token =>
        {
            string fullPath = Path.GetFullPath(path);
            var previous = intent == OpenIntent.Open ? null : archive;
            var expected = previous?.Scan.Identity;
            string? candidatePassword = expected is null ? null : password;
            var selected = previous is null ? [] : SelectedIds();
            var anchor = selected.ToHashSet();
            if (previous is not null && anchor.Count == 0 && displayed.Length > 0) anchor.Add(ParseId(displayed[0].Row.Id));
            bool replaced = false;
            ArchiveScan scan;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var progress = new WindowProgress<int>(Dispatcher, count =>
                    {
                        if (!closed && !token.IsCancellationRequested) StatusText.Text = Text.Format("ScanProgress", count);
                    });
                    scan = await Task.Run(() => worker.ScanAsync(fullPath, progress, token, candidatePassword, requestedCodePage, expected), token);
                    break;
                }
                catch (ArchiveOperationException error) when (error.Code == "ArchiveChanged" && intent == OpenIntent.Refresh && expected is not null)
                {
                    expected = null; candidatePassword = null; requestedCodePage = 0; replaced = true;
                }
                catch (ArchiveOperationException error) when (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsVisible) BringForward();
                    var prompt = new PasswordDialog(this, error.Code);
                    using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (prompt.IsVisible) prompt.Close(); })));
                    bool accepted = prompt.ShowDialog() == true;
                    candidatePassword = accepted ? prompt.Value : null; prompt.Clear();
                    if (!accepted) throw new OperationCanceledException();
                }
            }
            token.ThrowIfCancellationRequested();
            StatusText.Text = Text["BuildingIndex"];
            var result = await Task.Run(() =>
            {
                var presentation = new ArchivePresentation(scan, token);
                var tree = new ArchiveFolderTree(Path.GetFileName(fullPath), presentation.Index, token);
                var map = previous is null ? null : ArchiveReloadMap.Create(previous, presentation, token);
                string? location = map?.Directories.GetValueOrDefault(view.Directory);
                var next = location is null ? new View("", "", false, view.Sort, view.Descending, 0) : view with { Directory = location };
                if (map is not null && folderTree is not null) tree.RestoreExpansion(folderTree, map.Directories, token);
                var restored = location is null ? [] : TranslateIds(selected, map!).ToHashSet();
                var page = presentation.Query(Request(next.Directory, next.Search, next.Offset, SortKeys[next.Sort], next.Descending),
                    token, next.Local, EntryView.LabelFor, restoreIds: location is null ? null : TranslateIds(anchor, map!).ToHashSet());
                return (presentation, page, tree, next, map, location, restored);
            }, token);
            return () =>
            {
                archive = result.presentation; archiveName = Path.GetFileName(fullPath); OpenedPath = fullPath; password = candidatePassword;
                if (result.location is null || previous?.Scan.NameCodePage != scan.NameCodePage) { LastExtraction = null; retryDestination = null; }
                ((ClientApp)Application.Current).RegisterOpened(this, fullPath, scan.Identity?.FileId);
                view = result.next with { Offset = result.page.Offset }; directory = view.Directory; codePage = requestedCodePage;
                if (result.map is null) { history.Clear(); future.Clear(); }
                else { RemapHistory(history, result.map); RemapHistory(future, result.map); }
                LocalSearchBox.IsChecked = view.Local; PathColumn.Visibility = view.Search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                Title = archiveName + " — AileArc";
                folderTree = result.tree; currentFolder = null; TreeWelcome.Visibility = Visibility.Collapsed;
                AddressInput.Visibility = Visibility.Collapsed;
                SearchBox.Text = view.Search; ApplyPage(result.page);
                RestoreSelection(result.restored);
                StatusText.Text = replaced ? Text["ClientRefreshedReplacement"] : previous is not null && result.location is null ? Text["ClientReloadLayoutChanged"] :
                    result.location is not null && EntryGrid.SelectedItems.Count < selected.Length ? Text.Format("ClientReloadSelection", EntryGrid.SelectedItems.Count, selected.Length) :
                    Text.Format("ClientStatus", scan.Format, result.presentation.Index.Entries.Count);
            };
        }, false);
    }
    private static long ParseId(string id) => long.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
    private static IEnumerable<long> TranslateIds(IEnumerable<long> ids, ArchiveReloadMap map)
    {
        foreach (long id in ids) if (map.TranslateId(id) is long translated) yield return translated;
    }
    private static void RemapHistory(List<(View Page, string[] Selected)> trail, ArchiveReloadMap map)
    {
        for (int i = trail.Count - 1; i >= 0; i--)
        {
            var (page, selected) = trail[i];
            if (!map.Directories.TryGetValue(page.Directory, out var path)) { trail.RemoveAt(i); continue; }
            trail[i] = (page with { Directory = path }, TranslateIds(selected.Select(ParseId), map)
                .Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        }
    }
    private void RestoreSelection(IReadOnlySet<long> ids)
    {
        if (ids.Count == 0) return;
        foreach (var row in displayed.Where(r => ids.Contains(ParseId(r.Row.Id)))) EntryGrid.SelectedItems.Add(row);
        if (EntryGrid.SelectedItem is EntryView first) EntryGrid.ScrollIntoView(first);
    }
    private static BrowserRequest Request(string directory, string search, int offset, string sort, bool descending) => new(1, Guid.NewGuid().ToString(), "query", Directory: directory, Search: search, Sort: sort, Descending: descending, Offset: offset);
    private async Task QueryAsync(string targetDirectory, int targetOffset, bool clearSearch = false, bool submit = false, bool back = false, bool forward = false, int? sort = null, bool? descending = null, CancellationToken? live = null, long? revealId = null, bool previousPage = false)
    {
        if ((back && history.Count == 0) || (forward && future.Count == 0)) return;
        var target = back ? history[^1].Page : forward ? future[^1].Page : new View(targetDirectory, clearSearch ? "" : submit ? SearchBox.Text : view.Search,
            LocalSearchBox.IsChecked == true, sort ?? view.Sort, descending ?? view.Descending, targetOffset);
        var current = archive;
        if (target.Search.Length > 512 || current is null || IsBusy) return;
        var selected = EntryGrid.SelectedItems.Cast<EntryView>().Select(row => row.Row.Id).ToArray();
        var anchors = back ? history[^1].Selected : forward ? future[^1].Selected : null;
        async Task<Action> Prepare(CancellationToken token)
        {
            var page = await Task.Run(() => current.Query(Request(target.Directory, target.Search, target.Offset,
                SortKeys[target.Sort], target.Descending), token, target.Local, EntryView.LabelFor, revealId, previousPage, anchors?.Select(ParseId).ToHashSet()), token);
            return () =>
            {
                string[] restore = revealId.HasValue ? [revealId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];
                if (back || forward)
                {
                    var source = back ? history : future; var destination = back ? future : history;
                    destination.Add((view, selected)); restore = source[^1].Selected; source.RemoveAt(source.Count - 1);
                }
                else if (target.Directory != view.Directory || target.Search != view.Search || target.Local != view.Local && target.Search.Length > 0)
                {
                    future.Clear();
                    // Refining an active search is one navigation step, even with pauses between letters.
                    if (!(live.HasValue && view.Directory == target.Directory && view.Local == target.Local && view.Search.Length > 0 && target.Search.Length > 0))
                    {
                        if (history.Count == 32) history.RemoveAt(0);
                        history.Add((view, selected));
                    }
                }
                view = target with { Offset = page.Offset }; directory = target.Directory;
                if (clearSearch || back || forward) SearchBox.Text = target.Search;
                PathColumn.Visibility = target.Search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                ApplyPage(page, revealFolder: clearSearch || back || forward);
                RestoreSelection(restore.Select(ParseId).ToHashSet());
            };
        }
        if (live is { } latest)
        {
            var commit = await Prepare(latest);
            latest.ThrowIfCancellationRequested();
            if (!closed && !IsBusy && !composing && ReferenceEquals(current, archive) && SearchBox.Text == target.Search) commit();
            return;
        }
        await RunAsync(Text["BuildingIndex"], Prepare);
        // Commit controls to the view actually displayed, including after cancellation.
        configuring = true;
        UpdateHeaders(); LocalSearchBox.IsChecked = view.Local;
        configuring = false;
    }
    private void ApplyPage(BrowserPage page, bool revealFolder = false)
    {
        offset = page.Offset; total = page.Total;
        ApplyRows(page.Rows.Select(row => new EntryView(row)).ToArray()); UpdateNavigation(); SyncFolderTree(revealFolder);
    }
    internal void ApplyRows(EntryView[] rows)
    {
        EntryGrid.ContextMenu.IsOpen = false;
        changingItems = true;
        try { EntryGrid.UnselectAll(); displayed = rows; EntryGrid.ItemsSource = rows; }
        finally { changingItems = false; }
        if (rows.Length > 0) EntryGrid.ScrollIntoView(rows[0]);
        EmptyPanel.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeMark.Visibility = Visibility.Collapsed;
        EmptyHeading.Text = Text[view.Search.Length > 0 ? "NoResults" : archive?.Scan.Entries.Count == 0 ? "ClientArchiveEmpty" : "ClientEmptyFolder"];
        EmptyText.Text = view.Search.Length == 0 ? "" : Text["ClientTryAnotherSearch"];
        EmptyAction.Content = Text["ClientClearSearch"]; EmptyAction.Visibility = view.Search.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelection();
    }
    private void SetControls()
    {
        bool idle = !IsBusy && ((ClientApp)Application.Current).CanStart(this);
        EmptyAction.IsEnabled = idle; SetActivity(IsBusy);
        OpenButton.IsEnabled = idle; LanguageBox.IsEnabled = idle; RestartButton.IsEnabled = idle;
        SearchBox.IsEnabled = idle && archive is not null;
        LocalSearchBox.IsEnabled = SearchBox.IsEnabled;
        ClearSearch.IsEnabled = SearchBox.IsEnabled;
        if (!idle) CancelLiveSearch();
        else if (SearchBox.Text != view.Search && pendingSearch is null) ScheduleSearch();
        configuring = true; EncodingBox.SelectedIndex = Array.IndexOf(CodePages, codePage); configuring = false;
        EncodingBox.IsEnabled = idle && archive?.Scan.Format == "ZIP";
        EntryGrid.IsEnabled = idle; Folders.IsEnabled = idle && archive is not null;
        UpdateNavigation(); UpdateSelection();
        ExtractButton.IsEnabled = ExtractMore.IsEnabled = idle && archive is not null;
        TestArchiveButton.IsEnabled = idle && archive is not null;
        ResultButton.Visibility = idle && LastExtraction is not null ? Visibility.Visible : Visibility.Collapsed;
        CreatedFolderButton.Visibility = idle && LastCreated is not null ? Visibility.Visible : Visibility.Collapsed;
        CopiesButton.IsEnabled = idle;
        CreateButton.IsEnabled = RecoveryButton.IsEnabled = idle;
    }
    private void UpdateNavigation()
    {
        RefreshAddress(); UpdateHeaders();
        ForwardButton.IsEnabled = !IsBusy && future.Count > 0;
        UpButton.IsEnabled = !IsBusy && directory.Length > 0;
        BackButton.IsEnabled = !IsBusy && history.Count > 0;
        RefreshButton.IsEnabled = !IsBusy && archive is not null;
        PreviousButton.IsEnabled = !IsBusy && offset > 0;
        NextButton.IsEnabled = !IsBusy && offset + displayed.Length < total;
        SummaryText.Text = archive is null ? Text["WelcomeDetail"] : total == 0 ? Text["NoResults"] :
            Text.Format("ClientSummary", total, offset + 1, offset + displayed.Length);
    }
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (EntryView row in e.RemovedItems) row.IsSelected = false;
        foreach (EntryView row in e.AddedItems) row.IsSelected = true;
        if (!changingItems) UpdateSelection();
    }
    private void UpdateSelection()
    {
        int count = EntryGrid.SelectedItems.Count;
        ExtractButton.Content = ExtractionLabel(count);
        SelectAllBox.IsChecked = count == 0 ? false : count == displayed.Length ? true : null;
        SelectionText.Text = Text.Format("ClientSelectionCount", displayed.Length, count);
        var row = count == 1 ? EntryGrid.SelectedItem as EntryView : null;
        DetailName.Text = count > 1 ? Text.Format("ClientManySelected", count) : row?.Name ?? Text["ClientSelectHint"];
        DetailProperties.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailClear.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPath.Text = row?.Path ?? "";
        DetailType.Text = row?.TypeLabel ?? "";
        DetailSize.Text = row?.Row.Type != "folder" && row?.Row.UnpackedBytes is ulong bytes ? Text.Format("ClientExactSize", bytes) : "—";
        bool enabled = row is not null && !IsBusy && archive is not null && ((ClientApp)Application.Current).CanStart(this);
        OpenFileButton.IsEnabled = enabled && row!.Row.Type != "folder";
        CopyPathButton.IsEnabled = enabled;
    }
    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        bool hide = DetailPane.Visibility == Visibility.Visible;
        DetailColumn.Width = new GridLength(hide ? 0 : 254); DetailDivider.Width = new GridLength(hide ? 0 : 1);
        DetailPane.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        ShowDetails.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        if (!hide) FadeIn(DetailPane);
        (hide ? ShowDetails : HideDetails).Focus();
    }
    internal Action<string> CopyText { get; set; } = Clipboard.SetText;
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyPathButton.IsEnabled || EntryGrid.SelectedItems.Count != 1 || EntryGrid.SelectedItem is not EntryView row) return;
        try { CopyText(row.Path); StatusText.Text = Text["ClientPathCopied"]; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = Text["ClientClipboardBusy"]; }
    }
    private void RowCheck_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { DataContext: EntryView row }) return;
        e.Handled = true; ((CheckBox)sender).Focus();
        if (EntryGrid.SelectedItems.Contains(row)) EntryGrid.SelectedItems.Remove(row); else EntryGrid.SelectedItems.Add(row);
    }
    private void All_Click(object sender, RoutedEventArgs e) => ToggleAll();
    internal void ToggleAll() { if (EntryGrid.SelectedItems.Count == displayed.Length) EntryGrid.UnselectAll(); else EntryGrid.SelectAll(); UpdateSelection(); }
    private void Clear_Click(object sender, RoutedEventArgs e) => EntryGrid.UnselectAll();
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Text["Open"], Filter = Text["ClientFilter"], CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != true) return;
        await ((ClientApp)Application.Current).OpenPathAsync(dialog.FileName, preferred: this);
    }
    private async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (configuring) return;
        try { await new AppSettings(LanguageBox.SelectedIndex == 1 ? "en-US" : "zh-CN").SaveAsync(); StatusText.Text = Text["ClientLanguageSaved"]; RestartButton.Visibility = Visibility.Visible; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusText.Text = Text["SettingsFailed"]; }
    }
    private async void Restart_Click(object sender, RoutedEventArgs e) { if (Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsBusy)) StatusText.Text = Text["TaskRunning"]; else await ((ClientApp)Application.Current).RestartAsync(); }
    private async void Encoding_Changed(object sender, SelectionChangedEventArgs e)
    { if (!configuring && !IsBusy && archive?.Scan.Format == "ZIP" && EncodingBox.SelectedIndex >= 0) await ReopenArchiveAsync(CodePages[EncodingBox.SelectedIndex], OpenIntent.ChangeEncoding); }
    private async void Up_Click(object sender, RoutedEventArgs e) => await GoUpAsync();
    internal Task ActivateAsync()
    {
        if (IsBusy || EntryGrid.SelectedItems.Count != 1 || EntryGrid.SelectedItem is not EntryView row) return Task.CompletedTask;
        if (row.Row.Type == "folder") return QueryAsync(row.Path, 0, true);
        return OpenExternalAsync(long.Parse(row.Row.Id, System.Globalization.CultureInfo.InvariantCulture));
    }
    private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e) { if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null) await ActivateAsync(); }
    private async void Grid_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { EntryGrid.UnselectAll(); e.Handled = true; } else if (e.Key == Key.Enter) { e.Handled = true; await ActivateAsync(); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, offset, previousPage: true);
    private async void Next_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, offset + displayed.Length);
    private async void Back_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, 0, back: true);
    private void Scope_Click(object sender, RoutedEventArgs e) => ScheduleSearch(immediate: true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancel();
    private void RequestCancel()
    {
        if (operation is not { IsCancellationRequested: false } active) return;
        CancelButton.IsEnabled = false;
        StatusText.Text = Text["ClientCancelling"];
        active.Cancel();
    }
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    { while (element is not null) { if (element is T result) return result; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element); } return null; }
    internal DataGrid TestGrid => EntryGrid;
    internal bool? TestAllState => SelectAllBox.IsChecked;
    internal async Task TestOpenAsync(string path) => await OpenAsync(path);
    internal bool TestHasArchive => archive is not null;
    internal void RefreshControls() => SetControls();
    internal Task OpenArchiveAsync(string path, int codePage) => OpenAsync(path, codePage);
    internal void BringForward()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!ShowActivated) return;
        Activate();
        OwnedWindows.OfType<Window>().LastOrDefault()?.Activate();
    }
}
