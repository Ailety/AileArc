using System.Globalization;
using AileArc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using AileArc.Core.WorkCopies;

namespace AileArc.UI;

public sealed partial class MainWindow : Window
{
    private readonly LanguageService text;
    private readonly Grid root = new() { Padding = new Thickness(28, 20, 28, 18), RowSpacing = 16 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock archiveTitle = new() { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 20 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended, IsItemClickEnabled = false };
    private readonly TextBox search = new() { MinWidth = 200 };
    private readonly CheckBox localSearch = new();
    private readonly CheckBox descending = new();
    private readonly ComboBox sort = new() { MinWidth = 140, SelectedIndex = 0 };
    private readonly ComboBox languages = new() { MinWidth = 180 };
    private readonly ComboBox filenameEncoding = new() { MinWidth = 170 };
    private readonly int[] nameCodePages = [0, 65001, 936, 932];
    private bool changingEncoding;
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed };
    private readonly StackPanel crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Button back;
    private readonly Button up;
    private readonly Button open;
    private readonly Button cancel;
    private readonly Button extract;
    private readonly Button smartExtract;
    private readonly Button restart;
    private readonly Button extractSelected;
    private readonly Button errorDetails;
    private readonly Button workCopies;
    private readonly ArchiveWorkerClient client;
    private readonly Stack<NavigationState> history = new();
    private ArchiveIndex? index;
    private ArchiveScan? scan;
    private string directory = "";
    private string archivePath = "";
    private CancellationTokenSource? operation;
    private CancellationTokenSource? query;
    private long generation;
    private bool closed;
    private bool changingNavigation;
    private string? sessionPassword;
    private sealed record NavigationState(string Directory, string Search, long? SelectedId);

    public MainWindow(LanguageService language)
    {
        text = language;
        client = new ArchiveWorkerClient(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
        Title = "AileArc";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 760));
        for (int i = 0; i < 8; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Content = root;

        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "AileArc", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        archiveTitle.Text = text["Welcome"];
        heading.Children.Add(archiveTitle);
        heading.Children.Add(new TextBlock { Text = text["AppSubtitle"], Opacity = 0.65 });
        header.Children.Add(heading);
        open = Button(text["Open"], async () => await PickArchiveAsync());
        open.VerticalAlignment = VerticalAlignment.Center;
        extract = Button(text["ExtractTo"], async () => await ExtractAsync(false));
        smartExtract = Button(text["SmartExtract"], async () => await ExtractAsync(true));
        extractSelected = Button(text["ExtractSelected"], async () => await ExtractAsync(false, selectedOnly: true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(open);
        actions.Children.Add(extract);
        actions.Children.Add(smartExtract);
        actions.Children.Add(extractSelected);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        Add(header, 0);

        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        back = Button("←", async () => await BackAsync());
        up = Button("↑", async () => await NavigateAsync(ArchiveIndex.ParentOf(directory)));
        AutomationProperties.SetName(back, text["Back"]);
        AutomationProperties.SetName(up, text["Up"]);
        ToolTipService.SetToolTip(back, text["Back"]);
        ToolTipService.SetToolTip(up, text["Up"]);
        navigation.Children.Add(back);
        navigation.Children.Add(up);
        navigation.Children.Add(new ScrollViewer { Content = crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = 760 });
        Add(navigation, 1);

        var filters = new Grid { ColumnSpacing = 12 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        search.PlaceholderText = text["Search"];
        AutomationProperties.SetName(search, text["Search"]);
        localSearch.Content = text["CurrentDirectory"];
        descending.Content = text["Descending"];
        sort.Items.Add(text["SortName"]);
        sort.Items.Add(text["SortSize"]);
        sort.SelectedIndex = 0;
        AutomationProperties.SetName(sort, text["SortName"]);
        var filterControls = new FrameworkElement[] { search, localSearch, sort, descending };
        for (int i = 0; i < filterControls.Length; i++) { Grid.SetColumn(filterControls[i], i); filters.Children.Add(filterControls[i]); }
        Add(filters, 2);
        Add(progress, 3);

        var columns = new Grid { Padding = new Thickness(14, 0, 14, 0), ColumnSpacing = 12 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var titles = new[] { "Name", "Size", "Path" };
        for (int i = 0; i < titles.Length; i++) { var label = new TextBlock { Text = text[titles[i]], Opacity = 0.65 }; Grid.SetColumn(label, i); columns.Children.Add(label); }
        Add(columns, 4);

        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Padding="8,9" ColumnSpacing="12">
                <Grid.ColumnDefinitions><ColumnDefinition Width="2*"/><ColumnDefinition Width="110"/><ColumnDefinition Width="3*"/></Grid.ColumnDefinitions>
                <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="{Binding Glyph}" FontSize="16"/><TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"/></StackPanel>
                <TextBlock Grid.Column="1" Text="{Binding SizeText}" Opacity="0.75"/>
                <TextBlock Grid.Column="2" Text="{Binding Path}" TextTrimming="CharacterEllipsis" Opacity="0.65"/>
              </Grid>
            </DataTemplate>
            """);
        list.ItemContainerStyle = new Style(typeof(ListViewItem));
        list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.DoubleTapped += async (_, _) => await ActivateSelectionAsync();
        list.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; await ActivateSelectionAsync(); } };
        list.SelectionChanged += (_, _) => UpdateSelectionActions();
        var listPanel = new Grid();
        listPanel.Children.Add(list);
        empty.Text = text["WelcomeDetail"];
        empty.TextWrapping = TextWrapping.Wrap;
        empty.IsHitTestVisible = false;
        listPanel.Children.Add(empty);
        Add(listPanel, 5);

        var bottom = new Grid { ColumnSpacing = 12 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(status);
        cancel = Button(text["Cancel"], () => { operation?.Cancel(); return Task.CompletedTask; });
        Grid.SetColumn(cancel, 1);
        bottom.Children.Add(cancel);
        languages.Items.Add("简体中文");
        languages.Items.Add("English (United States)");
        languages.SelectedIndex = text.Language == "en-US" ? 1 : 0;
        AutomationProperties.SetName(languages, text["Language"]);
        ToolTipService.SetToolTip(languages, text["Language"]);
        languages.SelectionChanged += async (_, _) =>
        {
            try
            {
                await new AppSettings(languages.SelectedIndex == 1 ? "en-US" : "zh-CN").SaveAsync();
                notice.Text = text["RestartNotice"];
                if (restart is not null) restart.Visibility = Visibility.Visible;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notice.Text = text["SettingsFailed"]; }
        };
        Grid.SetColumn(languages, 2);
        bottom.Children.Add(languages);
        Add(bottom, 6);
        notice.Text = text["ReadOnlyNotice"];
        var notices = new StackPanel { Spacing = 8 };
        notices.Children.Add(notice);
        restart = Button(text["RestartNow"], () => ConfirmCloseAsync(restartApplication: true));
        restart.Visibility = Visibility.Collapsed;
        errorDetails = Button(text["ErrorDetails"], ShowErrorDetailsAsync);
        errorDetails.Visibility = Visibility.Collapsed;
        workCopies = Button(text["WorkCopies"], ShowWorkCopiesAsync);
        var secondaryActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        secondaryActions.Children.Add(workCopies);
        secondaryActions.Children.Add(errorDetails);
        secondaryActions.Children.Add(restart);
        foreach (var label in new[] { text["FilenameEncodingAuto"], "UTF-8 (65001)", "GBK (936)", "Shift-JIS (932)" }) filenameEncoding.Items.Add(label);
        filenameEncoding.SelectedIndex = 0;
        AutomationProperties.SetName(filenameEncoding, text["FilenameEncoding"]);
        ToolTipService.SetToolTip(filenameEncoding, text["FilenameEncoding"]);
        filenameEncoding.SelectionChanged += async (_, _) =>
        {
            if (!changingEncoding && scan?.Format == "ZIP" && operation is null && !dialogOpen)
                await OpenArchiveAsync(archivePath, sessionPassword, nameCodePages[filenameEncoding.SelectedIndex]);
        };
        secondaryActions.Children.Add(filenameEncoding);
        notices.Children.Add(secondaryActions);
        Add(notices, 7);

        search.TextChanged += async (_, _) => { if (!changingNavigation) await RefreshAsync(debounce: true); };
        localSearch.Click += async (_, _) => await RefreshAsync();
        descending.Click += async (_, _) => await RefreshAsync();
        sort.SelectionChanged += async (_, _) => await RefreshAsync();
        var openShortcut = new KeyboardAccelerator { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control };
        openShortcut.Invoked += async (_, e) => { e.Handled = true; await PickArchiveAsync(); };
        root.KeyboardAccelerators.Add(openShortcut);
        var searchShortcut = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        searchShortcut.Invoked += (_, e) => { e.Handled = true; search.Focus(FocusState.Keyboard); };
        root.KeyboardAccelerators.Add(searchShortcut);
        Closed += (_, _) => { closed = true; operation?.Cancel(); query?.Cancel(); workCopyTimer.Stop(); };
        AppWindow.Closing += (sender, args) =>
        {
            if ((operation is not null || openedCopies.Count > 0) && !allowClose) { args.Cancel = true; _ = ConfirmCloseAsync(); }
        };
        InitializeWorkCopyMonitoring();
        SetBusy(false);
        UpdateBreadcrumbs();
    }

    private static Button Button(string content, Func<Task> action)
    {
        var result = new Button { Content = content };
        result.Click += async (_, _) => await action();
        return result;
    }
    private void Add(FrameworkElement control, int row) { Grid.SetRow(control, row); root.Children.Add(control); }
    private async Task PickArchiveAsync()
    {
        if (dialogOpen || operation is not null) return;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await OpenArchiveAsync(file.Path);
        }
        catch (Exception) { if (!closed) status.Text = text["OpenFailed"]; }
    }

    public async Task OpenArchiveAsync(string path, string? password = null, int nameCodePage = 0)
    {
        operation?.Cancel();
        query?.Cancel();
        var current = new CancellationTokenSource();
        operation = current;
        long version = ++generation;
        index = null;
        scan = null;
        directory = "";
        history.Clear();
        changingNavigation = true;
        search.Text = "";
        changingNavigation = false;
        list.ItemsSource = null;
        empty.Visibility = Visibility.Collapsed;
        archivePath = path;
        sessionPassword = password;
        changingEncoding = true;
        filenameEncoding.SelectedIndex = Array.IndexOf(nameCodePages, nameCodePage);
        changingEncoding = false;
        archiveTitle.Text = System.IO.Path.GetFileName(path);
        status.Text = text["Scanning"];
        SetFailures([]);
        SetBusy(true);
        UpdateBreadcrumbs();
        try
        {
            var report = new Progress<int>(count => { if (version == generation && !closed && !current.IsCancellationRequested) status.Text = text.Format("ScanProgress", count); });
            var result = await client.ScanAsync(path, report, current.Token, password, nameCodePage);
            if (version != generation || closed) return;
            status.Text = text["BuildingIndex"];
            var built = await Task.Run(() => new ArchiveIndex(result.Entries, current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (version != generation || closed) return;
            scan = result;
            index = built;
            await RefreshAsync();
        }
        catch (OperationCanceledException) { if (version == generation && !closed) status.Text = text["Cancelled"]; }
        catch (ArchiveOperationException error)
        {
            if (version == generation && !closed)
            {
                ShowFailure(error.Code, System.IO.Path.GetFileName(path));
                if (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
                {
                    string? entered = await AskPasswordAsync(error.Code);
                    if (entered is not null && !current.IsCancellationRequested) await OpenArchiveAsync(path, entered, nameCodePage);
                }
            }
        }
        catch (Exception) { if (version == generation && !closed) ShowFailure("OpenFailed", System.IO.Path.GetFileName(path)); }
        finally
        {
            if (version == generation && !closed) { SetBusy(false); UpdateBreadcrumbs(); }
            if (ReferenceEquals(operation, current)) operation = null;
            current.Dispose();
        }
    }

    private async Task RefreshAsync(bool debounce = false, long? selectedId = null)
    {
        query?.Cancel();
        if (index is null) return;
        var current = new CancellationTokenSource();
        query = current;
        var snapshot = index;
        string searchText = search.Text;
        string folder = directory;
        bool local = localSearch.IsChecked == true;
        bool reverse = descending.IsChecked == true;
        var order = sort.SelectedIndex == 1 ? EntrySort.Size : EntrySort.Name;
        try
        {
            if (debounce) await Task.Delay(160, current.Token);
            var result = await Task.Run(() => snapshot.Query(folder, searchText, local, order, reverse, current.Token), current.Token);
            var rows = await Task.Run(() => result.Select(e => new DisplayEntry(e)).ToArray(), current.Token);
            if (current.IsCancellationRequested || closed || !ReferenceEquals(index, snapshot)) return;
            list.ItemsSource = rows;
            if (selectedId is not null) list.SelectedItem = rows.FirstOrDefault(e => e.Entry.Id == selectedId);
            empty.Text = text["NoResults"];
            empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (scan is not null) status.Text = text.Format("Ready", scan.Format, scan.Entries.Count, rows.Length);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (current.IsCancellationRequested) { }
        finally { if (ReferenceEquals(query, current)) query = null; current.Dispose(); }
    }

    private async Task ActivateSelectionAsync()
    {
        if (operation is not null || dialogOpen || closed) return;
        if (list.SelectedItem is not DisplayEntry item) return;
        if (item.Entry.IsDirectory) await NavigateAsync(item.Entry.Path);
        else await OpenExternalAsync(item.Entry.Id);
    }
    private async Task NavigateAsync(string destination)
    {
        if (index is null) return;
        history.Push(new NavigationState(directory, search.Text, (list.SelectedItem as DisplayEntry)?.Entry.Id));
        directory = destination;
        changingNavigation = true;
        search.Text = "";
        changingNavigation = false;
        UpdateBreadcrumbs();
        await RefreshAsync();
    }
    private async Task BackAsync()
    {
        if (!history.TryPop(out var state)) return;
        directory = state.Directory;
        changingNavigation = true;
        search.Text = state.Search;
        changingNavigation = false;
        UpdateBreadcrumbs();
        await RefreshAsync(selectedId: state.SelectedId);
    }
    private void UpdateBreadcrumbs()
    {
        crumbs.Children.Clear();
        crumbs.Children.Add(Button(archivePath.Length == 0 ? text["Root"] : System.IO.Path.GetFileName(archivePath), async () => await NavigateAsync("")));
        string path = "";
        foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "/" + part;
            string target = path;
            crumbs.Children.Add(new TextBlock { Text = "/", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 });
            crumbs.Children.Add(Button(part, async () => await NavigateAsync(target)));
        }
        back.IsEnabled = history.Count > 0 && index is not null;
        up.IsEnabled = directory.Length > 0 && index is not null;
    }
    private void SetBusy(bool busy)
    {
        if (busy) idleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        else idleCompletion.TrySetResult();
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        cancel.IsEnabled = busy;
        search.IsEnabled = !busy && index is not null;
        open.IsEnabled = !busy;
        extract.IsEnabled = smartExtract.IsEnabled = !busy && index is not null;
        languages.IsEnabled = !busy;
        restart.IsEnabled = !busy;
        workCopies.IsEnabled = !busy;
        filenameEncoding.IsEnabled = !busy && scan?.Format == "ZIP";
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        if (extractSelected is not null) extractSelected.IsEnabled = operation is null && scan is not null && list.SelectedItems.Count > 0;
    }
}

[System.ComponentModel.Bindable(true)]
public sealed class DisplayEntry(BrowserEntry entry)
{
    public BrowserEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string Glyph => Entry.IsDirectory ? "\uE8B7" : "\uE7C3";
    public string SizeText => Entry.IsDirectory ? "—" : Entry.Size?.ToString("N0", CultureInfo.CurrentCulture) is string size ? size + " B" : "—";
    public override string ToString() => Name;
}
