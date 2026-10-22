using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AileArc.Core;
using AileArc.DesktopBridge;

namespace AileArc.WpfValidation;

public partial class MainWindow : Window
{
    private readonly ArchiveWorkerClient worker = new(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
    private readonly string? initialPath;
    private ArchivePresentation? archive;
    private EntryView[] source = [], displayed = [];
    private CancellationTokenSource? operation;
    private string directory = "", archiveName = "";
    private int generation, offset, total;
    private bool synthetic, composing, closed, changingItems;

    public MainWindow(string? path = null)
    {
        initialPath = path;
        InitializeComponent();
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SearchBox, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(SearchBox, (_, _) => composing = false);
        SearchBox.LostKeyboardFocus += (_, _) => composing = false;
        Loaded += async (_, _) => { if (initialPath is null) await LoadSampleAsync(1000); else await OpenAsync(initialPath); };
        Closed += (_, _) => { closed = true; generation++; operation?.Cancel(); };
    }
    private async Task RunAsync(string status, Func<CancellationToken, Task<Action>> prepare)
    {
        operation?.Cancel();
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        int version = ++generation;
        StatusText.Text = status; CancelButton.Visibility = Visibility.Visible;
        var clock = Stopwatch.StartNew();
        try
        {
            Action commit = await prepare(cancellation.Token);
            if (closed || version != generation || cancellation.IsCancellationRequested) return;
            commit();
            StatusText.Text = synthetic ? "生成样本 · 仅用于界面压力验证；行控件使用虚拟化" : "现有 Core / Worker · 只读 · 每页最多 200 项";
            Trace.WriteLine($"WPF validation operationMs={clock.ElapsedMilliseconds}; displayed={displayed.Length}");
        }
        catch (OperationCanceledException) { if (!closed && version == generation) StatusText.Text = "已取消，保留上次结果"; }
        catch (Exception error) when (cancellation.IsCancellationRequested && error is InvalidOperationException) { }
        catch (ArchiveOperationException error) { if (!closed && version == generation) StatusText.Text = error.Code is "PasswordRequired" or "WrongPasswordOrDamaged" ? "该归档需要密码；本轮纯 WPF 验证尚未接入密码对话框" : $"读取失败：{error.Code}"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { if (!closed && version == generation) StatusText.Text = "无法读取归档或请求超出验证限制"; }
        finally { if (ReferenceEquals(operation, cancellation)) { operation = null; if (!closed) CancelButton.Visibility = Visibility.Collapsed; } }
    }
    private Task OpenAsync(string path) => RunAsync("正在通过 Worker 读取归档…", async token =>
    {
        var scan = await worker.ScanAsync(Path.GetFullPath(path), cancellationToken: token);
        var result = await Task.Run(() =>
        {
            var presentation = new ArchivePresentation(scan, token);
            return (presentation, page: presentation.Query(Request("", "", 0), token), folders: presentation.GetRootFolders(token));
        }, token);
        return () =>
        {
            archive = result.presentation; archiveName = Path.GetFileName(path); synthetic = false; source = []; directory = "";
            RootButton.Content = archiveName; Title = archiveName + " — AileArc · 纯 WPF 验证";
            SearchBox.Clear(); Folders.ItemsSource = result.folders; RealButton.IsEnabled = true;
            ApplyPage(result.page);
        };
    });
    private static BrowserRequest Request(string directory, string search, int offset) => new(1, Guid.NewGuid().ToString(), "query", Directory: directory, Search: search, Offset: offset);
    private Task QueryAsync(string targetDirectory, int targetOffset, bool clearSearch = false, bool forceArchive = false)
    {
        string search = clearSearch ? "" : SearchBox.Text;
        if (search.Length > 512) return Task.CompletedTask;
        if (synthetic && !forceArchive)
        {
            var snapshot = source;
            return RunAsync("正在筛选生成样本…", async token =>
            {
                var rows = await Task.Run(() => snapshot.Where(row => { token.ThrowIfCancellationRequested(); return row.Name.Contains(search, StringComparison.OrdinalIgnoreCase); }).ToArray(), token);
                return () => { offset = 0; total = rows.Length; ApplyRows(rows); UpdateNavigation(); };
            });
        }
        var current = archive;
        if (current is null) return Task.CompletedTask;
        return RunAsync("正在查询归档索引…", async token =>
        {
            var page = await Task.Run(() => current.Query(Request(targetDirectory, search, targetOffset), token), token);
            var folders = forceArchive ? await Task.Run(() => current.GetRootFolders(token), token) : null;
            return () =>
            {
                if (forceArchive) { synthetic = false; source = []; RootButton.Content = archiveName; Title = archiveName + " — AileArc · 纯 WPF 验证"; Folders.ItemsSource = folders; }
                directory = targetDirectory; if (clearSearch) SearchBox.Clear(); ApplyPage(page);
            };
        });
    }
    internal Task LoadSampleAsync(int count) => RunAsync("正在准备界面压力样本…", async token =>
    {
        var rows = await Task.Run(() =>
        {
            var values = new EntryView[count];
            for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); values[i] = new(new BrowserRow(i.ToString(), $"项目说明_{i}.md", "", $"项目说明_{i}.md", "md", 24, "—", false)); }
            return values;
        }, token);
        return () =>
        {
            synthetic = true; source = rows; directory = ""; offset = 0; total = count;
            RootButton.Content = "项目资料.zip · 生成样本"; Title = "AileArc · 纯 WPF 验证";
            SearchBox.Clear(); Folders.ItemsSource = null; ApplyRows(rows); UpdateNavigation();
        };
    });
    private void ApplyPage(BrowserPage page)
    {
        offset = page.Offset; total = page.Total;
        ApplyRows(page.Rows.Select(row => new EntryView(row)).ToArray()); UpdateNavigation();
    }
    private void ApplyRows(EntryView[] rows)
    {
        // Clear old models too: synthetic source objects survive filtered views.
        foreach (EntryView row in EntryGrid.SelectedItems.Cast<EntryView>().ToArray()) row.IsSelected = false;
        changingItems = true;
        try { EntryGrid.UnselectAll(); displayed = rows; EntryGrid.ItemsSource = rows; }
        finally { changingItems = false; }
        if (rows.Length > 0) EntryGrid.ScrollIntoView(rows[0]);
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }
    private void UpdateNavigation()
    {
        LocationText.Text = directory.Length == 0 ? "根目录" : directory;
        UpButton.IsEnabled = !synthetic && directory.Length > 0;
        PreviousButton.IsEnabled = !synthetic && offset > 0;
        NextButton.IsEnabled = !synthetic && offset + displayed.Length < total;
        SummaryText.Text = $"{(synthetic ? "生成样本" : "归档索引")} {(synthetic ? source.Length : archive?.Index.Entries.Count ?? 0):N0} 项\n结果 {total:N0} 项 · 起点 {offset:N0}";
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
        SelectionText.Text = $"当前 {displayed.Length:N0} 项 · 已选 {count:N0} 项";
        var row = EntryGrid.SelectedItem as EntryView;
        DetailName.Text = count > 1 ? $"已选择 {count:N0} 个项目" : row?.Name ?? "请选择一个项目";
        DetailPath.Text = count > 1 ? "支持 Ctrl / Shift 多选；页面切换会清除选择" : row?.Path ?? "复选框可独立切换选择";
        EnterButton.IsEnabled = !synthetic && count == 1 && row?.Row.Type == "folder";
    }
    private void RowCheck_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { DataContext: EntryView row }) return;
        e.Handled = true;
        ((CheckBox)sender).Focus();
        if (EntryGrid.SelectedItems.Contains(row)) EntryGrid.SelectedItems.Remove(row); else EntryGrid.SelectedItems.Add(row);
    }
    private void All_Click(object sender, RoutedEventArgs e) => ToggleAll();
    internal void ToggleAll() { if (EntryGrid.SelectedItems.Count == displayed.Length) EntryGrid.UnselectAll(); else EntryGrid.SelectAll(); UpdateSelection(); }
    private void Clear_Click(object sender, RoutedEventArgs e) => EntryGrid.UnselectAll();
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "打开压缩包", Filter = "压缩包|*.zip;*.7z;*.rar|所有文件|*.*", CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(this) == true) await OpenAsync(dialog.FileName);
    }
    private async void Sample_Click(object sender, RoutedEventArgs e) => await LoadSampleAsync(((Button)sender).Tag?.ToString() == "100000" ? 100000 : 1000);
    private async void Real_Click(object sender, RoutedEventArgs e)
    {
        if (archive is null) return;
        await QueryAsync("", 0, true, forceArchive: true);
    }
    private async void Root_Click(object sender, RoutedEventArgs e) => await QueryAsync("", 0, true);
    private async void Folder_Click(object sender, RoutedEventArgs e) => await QueryAsync(((Button)sender).Tag?.ToString() ?? "", 0, true);
    private async void Up_Click(object sender, RoutedEventArgs e) => await QueryAsync(ArchiveIndex.ParentOf(directory), 0, true);
    private async void Enter_Click(object sender, RoutedEventArgs e) => await EnterDirectoryAsync();
    private Task EnterDirectoryAsync() => !synthetic && EntryGrid.SelectedItems.Count == 1 && EntryGrid.SelectedItem is EntryView { Row.Type: "folder" } row ? QueryAsync(row.Path, 0, true) : Task.CompletedTask;
    private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e) { if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null) await EnterDirectoryAsync(); }
    private async void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EntryGrid.UnselectAll(); e.Handled = true; }
        else if (e.Key == Key.Enter) { e.Handled = true; await EnterDirectoryAsync(); }
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, Math.Max(0, offset - 200));
    private async void Next_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, offset + displayed.Length);
    private async void Search_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, 0);
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) composing = false;
        if (e.Key == Key.Enter && !composing) { e.Handled = true; await QueryAsync(directory, 0); }
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (SearchHint is not null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    { while (element is not null) { if (element is T result) return result; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element); } return null; }

    internal DataGrid ValidationGrid => EntryGrid;
    internal TextBox ValidationSearch => SearchBox;
    internal bool? ValidationAllState => SelectAllBox.IsChecked;
    internal int RealizedRowCount() => CountRows(EntryGrid);
    private static int CountRows(DependencyObject element)
    { int count = element is DataGridRow ? 1 : 0; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) count += CountRows(VisualTreeHelper.GetChild(element, i)); return count; }
}
