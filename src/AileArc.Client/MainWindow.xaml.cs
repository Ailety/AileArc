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
    private ArchivePresentation? archive;
    private EntryView[] displayed = [];
    private CancellationTokenSource? operation;
    private string directory = "", archiveName = "";
    private string? password;
    private int generation, offset, total, codePage;
    private bool composing, closed, changingItems, configuring = true;
    private LanguageService Text => ClientApp.Text;
    public string? OpenedPath { get; private set; }
    internal string? TargetPath => OpenedPath ?? initialPath;
    public bool IsBusy => operation is not null;

    public MainWindow(string? path = null)
    {
        initialPath = path;
        InitializeComponent();
        LanguageBox.ItemsSource = new[] { "简体中文", "English (United States)" };
        LanguageBox.SelectedIndex = Text.Language == "en-US" ? 1 : 0;
        SortBox.ItemsSource = new[] { Text["SortName"], Text["SortSize"] }; SortBox.SelectedIndex = 0;
        EncodingBox.ItemsSource = new[] { Text["FilenameEncodingAuto"], "UTF-8 (65001)", "GBK (936)", "Shift-JIS (932)" }; EncodingBox.SelectedIndex = 0;
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SearchBox, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(SearchBox, (_, _) => composing = false);
        SearchBox.LostKeyboardFocus += (_, _) => composing = false;
        EmptyText.Text = Text["WelcomeDetail"]; EmptyText.Visibility = Visibility.Visible;
        configuring = false; SetControls();
        Loaded += async (_, _) => { if (initialPath is not null) await OpenAsync(initialPath); };
        Closed += (_, _) => { closed = true; generation++; operation?.Cancel(); password = null; };
    }
    private async Task RunAsync(string status, Func<CancellationToken, Task<Action>> prepare)
    {
        operation?.Cancel();
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        int version = ++generation;
        StatusText.Text = status; CancelButton.Visibility = Visibility.Visible; SetControls();
        try
        {
            Action commit = await prepare(cancellation.Token);
            if (closed || version != generation || cancellation.IsCancellationRequested) return;
            commit();
            StatusText.Text = archive is null ? Text["ClientIdle"] : Text.Format("ClientStatus", archive.Scan.Format, archive.Index.Entries.Count);
        }
        catch (OperationCanceledException) { if (!closed && version == generation) StatusText.Text = Text["Cancelled"]; }
        catch (InvalidOperationException) when (cancellation.IsCancellationRequested) { }
        catch (ArchiveOperationException error) { if (!closed && version == generation) StatusText.Text = Text[error.Code]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!closed && version == generation) StatusText.Text = Text["OpenFailed"]; }
        finally
        {
            if (ReferenceEquals(operation, cancellation))
            {
                operation = null;
                if (!closed) { CancelButton.Visibility = Visibility.Collapsed; SetControls(); }
            }
        }
    }
    private Task OpenAsync(string path, int requestedCodePage = 0, string? existingPassword = null)
    {
        string order = SortBox.SelectedIndex == 1 ? "size" : "name";
        bool descending = DescendingBox.IsChecked == true;
        return RunAsync(Text["Scanning"], async token =>
        {
            string fullPath = Path.GetFullPath(path);
            string? candidatePassword = existingPassword;
            ArchiveScan scan;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { scan = await Task.Run(() => worker.ScanAsync(fullPath, cancellationToken: token, password: candidatePassword, nameCodePage: requestedCodePage), token); break; }
                catch (ArchiveOperationException error) when (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
                {
                    token.ThrowIfCancellationRequested();
                    var prompt = new PasswordDialog(this, error.Code);
                    bool accepted = prompt.ShowDialog() == true;
                    candidatePassword = accepted ? prompt.Value : null; prompt.Clear();
                    if (!accepted) throw new OperationCanceledException();
                }
            }
            var result = await Task.Run(() =>
            {
                var presentation = new ArchivePresentation(scan, token);
                return (presentation, page: presentation.Query(Request("", "", 0, order, descending), token), folders: presentation.GetRootFolders(token));
            }, token);
            return () =>
            {
                archive = result.presentation; archiveName = Path.GetFileName(fullPath); OpenedPath = fullPath; password = candidatePassword;
                directory = ""; codePage = requestedCodePage;
                RootButton.Content = archiveName; Title = archiveName + " — AileArc";
                SearchBox.Clear(); Folders.ItemsSource = result.folders; ApplyPage(result.page);
            };
        });
    }
    private static BrowserRequest Request(string directory, string search, int offset, string sort, bool descending) => new(1, Guid.NewGuid().ToString(), "query", Directory: directory, Search: search, Sort: sort, Descending: descending, Offset: offset);
    private Task QueryAsync(string targetDirectory, int targetOffset, bool clearSearch = false)
    {
        string search = clearSearch ? "" : SearchBox.Text;
        var current = archive;
        if (search.Length > 512 || current is null || IsBusy) return Task.CompletedTask;
        string order = SortBox.SelectedIndex == 1 ? "size" : "name";
        bool descending = DescendingBox.IsChecked == true;
        return RunAsync(Text["BuildingIndex"], async token =>
        {
            var page = await Task.Run(() => current.Query(Request(targetDirectory, search, targetOffset, order, descending), token), token);
            return () => { directory = targetDirectory; if (clearSearch) SearchBox.Clear(); ApplyPage(page); };
        });
    }
    private void ApplyPage(BrowserPage page)
    {
        offset = page.Offset; total = page.Total;
        ApplyRows(page.Rows.Select(row => new EntryView(row)).ToArray()); UpdateNavigation();
    }
    internal void ApplyRows(EntryView[] rows)
    {
        changingItems = true;
        try { EntryGrid.UnselectAll(); displayed = rows; EntryGrid.ItemsSource = rows; }
        finally { changingItems = false; }
        if (rows.Length > 0) EntryGrid.ScrollIntoView(rows[0]);
        EmptyText.Text = Text["NoResults"]; EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }
    private void SetControls()
    {
        bool idle = !IsBusy;
        OpenButton.IsEnabled = idle; LanguageBox.IsEnabled = idle; RestartButton.IsEnabled = idle;
        SearchBox.IsEnabled = idle && archive is not null; SortBox.IsEnabled = DescendingBox.IsEnabled = SearchBox.IsEnabled;
        configuring = true; EncodingBox.SelectedIndex = Array.IndexOf(CodePages, codePage); configuring = false;
        EncodingBox.IsEnabled = idle && archive?.Scan.Format == "ZIP";
        EntryGrid.IsEnabled = idle; RootButton.IsEnabled = idle; Folders.IsEnabled = idle;
        UpdateNavigation(); UpdateSelection();
    }
    private void UpdateNavigation()
    {
        LocationText.Text = directory.Length == 0 ? Text["Root"] : directory;
        UpButton.IsEnabled = !IsBusy && directory.Length > 0;
        PreviousButton.IsEnabled = !IsBusy && offset > 0;
        NextButton.IsEnabled = !IsBusy && offset + displayed.Length < total;
        SummaryText.Text = archive is null ? Text["WelcomeDetail"] : Text.Format("ClientSummary", archive.Index.Entries.Count, total, offset);
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
        SelectAllBox.IsChecked = count == 0 ? false : count == displayed.Length ? true : null;
        SelectionText.Text = Text.Format("ClientSelectionCount", displayed.Length, count);
        var row = EntryGrid.SelectedItem as EntryView;
        DetailName.Text = count > 1 ? Text.Format("ClientManySelected", count) : row?.Name ?? Text["ClientSelectHint"];
        DetailPath.Text = count > 1 ? Text["ClientPageSelection"] : row?.Path ?? Text["ClientCheckboxHint"];
        EnterButton.IsEnabled = !IsBusy && count == 1 && row?.Row.Type == "folder";
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
        if (archive is null) await OpenAsync(dialog.FileName); else ((ClientApp)Application.Current).OpenPath(dialog.FileName);
    }
    private async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (configuring) return;
        try { await new AppSettings(LanguageBox.SelectedIndex == 1 ? "en-US" : "zh-CN").SaveAsync(); StatusText.Text = Text["ClientLanguageSaved"]; RestartButton.Visibility = Visibility.Visible; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusText.Text = Text["SettingsFailed"]; }
    }
    private void Restart_Click(object sender, RoutedEventArgs e) { if (Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsBusy)) StatusText.Text = Text["TaskRunning"]; else ((ClientApp)Application.Current).Restart(); }
    private async void Encoding_Changed(object sender, SelectionChangedEventArgs e)
    { if (!configuring && !IsBusy && OpenedPath is { } path && archive?.Scan.Format == "ZIP" && EncodingBox.SelectedIndex >= 0) await OpenAsync(path, CodePages[EncodingBox.SelectedIndex], password); }
    private async void Sort_Changed(object sender, RoutedEventArgs e) { if (!configuring) await QueryAsync(directory, 0); }
    private async void Root_Click(object sender, RoutedEventArgs e) => await QueryAsync("", 0, true);
    private async void Folder_Click(object sender, RoutedEventArgs e) => await QueryAsync(((Button)sender).Tag?.ToString() ?? "", 0, true);
    private async void Up_Click(object sender, RoutedEventArgs e) => await QueryAsync(ArchiveIndex.ParentOf(directory), 0, true);
    private async void Enter_Click(object sender, RoutedEventArgs e) => await ActivateAsync();
    private Task ActivateAsync()
    {
        if (IsBusy || EntryGrid.SelectedItems.Count != 1 || EntryGrid.SelectedItem is not EntryView row) return Task.CompletedTask;
        if (row.Row.Type == "folder") return QueryAsync(row.Path, 0, true);
        StatusText.Text = Text["ClientOpenPending"]; return Task.CompletedTask;
    }
    private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e) { if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null) await ActivateAsync(); }
    private async void Grid_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { EntryGrid.UnselectAll(); e.Handled = true; } else if (e.Key == Key.Enter) { e.Handled = true; await ActivateAsync(); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, Math.Max(0, offset - 200));
    private async void Next_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, offset + displayed.Length);
    private async void Search_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, 0);
    private async void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) composing = false; if (e.Key == Key.Enter && !composing) { e.Handled = true; await QueryAsync(directory, 0); } }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (SearchHint is not null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    { while (element is not null) { if (element is T result) return result; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element); } return null; }
    internal DataGrid TestGrid => EntryGrid;
    internal bool? TestAllState => SelectAllBox.IsChecked;
    internal async Task TestOpenAsync(string path) => await OpenAsync(path);
    internal bool TestHasArchive => archive is not null;
}
