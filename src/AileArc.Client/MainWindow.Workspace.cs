using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Globalization;

namespace AileArc.Client;

public partial class MainWindow
{
    private static bool SystemAllowsMotion => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private void FadeIn(UIElement surface)
    {
        if (!SystemAllowsMotion || WindowState == WindowState.Minimized) return;
        surface.BeginAnimation(OpacityProperty, new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(120))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    private void InitializeWorkspaceActions()
    {
        SourceInitialized += (_, _) => SystemParameters.StaticPropertyChanged += MotionSettingChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= MotionSettingChanged;
        StateChanged += (_, _) => ApplyMotionPreference(SystemAllowsMotion);
        IsVisibleChanged += (_, _) => ApplyMotionPreference(SystemAllowsMotion);
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(NavigationCommands.GoToPage, Key.L, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(NavigationCommands.GoToPage, (_, _) => EditAddress(),
            (_, e) => e.CanExecute = archive is not null && !IsBusy && !composing));
        PreviewMouseDown += HistoryMouseDown;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, Open_Click, (_,e) => e.CanExecute = OpenButton.IsEnabled));
        EntryGrid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not EntryView item) return;
            if (!EntryGrid.SelectedItems.Contains(item)) EntryGrid.SelectedItem = item;
            EntryGrid.Focus(); e.Handled = true;
        };
        EntryGrid.ContextMenuOpening += (_, e) =>
        {
            if (IsBusy || !EntryGrid.IsEnabled || EntryGrid.SelectedItems.Count == 0 ||
                (e.CursorLeft >= 0 && FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null))
            { e.Handled = true; return; }
            var single = EntryGrid.SelectedItems.Count == 1 ? EntryGrid.SelectedItem as EntryView : null;
            RowOpen.IsEnabled = single != null;
            RowOpen.Header = Text[single?.Row.Type == "folder" ? "ClientEnter" : "ClientDefaultOpen"];
            RowLocate.IsEnabled = single != null && view.Search.Length > 0;
            RowParentExtract.IsEnabled = single is not null && single.Row.Type != "folder";
            RowExtract.Header = ExtractionLabel(EntryGrid.SelectedItems.Count);
        };
        EntryGrid.PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Handled || IsBusy || EntryGrid.SelectedItems.Count == 0) return;
            if (e.Key == Key.C) { e.Handled = true; CopySelectedPaths(); }
            if (e.Key == Key.E) { e.Handled = true; await ShowExtractDialogAsync(ExtractionMode.Selected); }
        };
    }
    private async void HistoryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || IsBusy || composing || OwnedWindows.Count > 0) return;
        bool back = e.ChangedButton == MouseButton.XButton1 && BackButton.IsEnabled;
        bool forward = e.ChangedButton == MouseButton.XButton2 && ForwardButton.IsEnabled;
        if (!back && !forward) return;
        e.Handled = true;
        await QueryAsync(directory, 0, back: back, forward: forward);
    }
    private void Density_Click(object sender, RoutedEventArgs e) => EntryGrid.RowHeight = CompactList.IsChecked == true ? 30 : 36;
    private void SetActivity(bool active)
    {
        if ((ActivityDot.Visibility == Visibility.Visible) == active) return;
        ActivityDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ApplyMotionPreference(SystemAllowsMotion);
    }
    private void MotionSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            ApplyMotionPreference(SystemAllowsMotion);
    }
    internal void ApplyMotionPreference(bool enabled)
    {
        bool visibleMotion = enabled && IsVisible && WindowState != WindowState.Minimized;
        if (!visibleMotion)
        {
            DetailPane.BeginAnimation(OpacityProperty, null);
            TreePane.BeginAnimation(OpacityProperty, null);
            DropHint.BeginAnimation(OpacityProperty, null);
        }
        bool pulse = visibleMotion && ActivityDot.Visibility == Visibility.Visible;
        if (pulse == ActivityDot.HasAnimatedProperties) return;
        ActivityDot.BeginAnimation(OpacityProperty, pulse ? new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(650))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
    }
    private async void Locate_Click(object sender, RoutedEventArgs e) => await LocateSelectedAsync();
    private async void ParentExtract_Click(object sender, RoutedEventArgs e) => await ShowExtractDialogAsync(ExtractionMode.ParentFolder);
    internal Task LocateSelectedAsync()
    {
        if (view.Search.Length == 0 || EntryGrid.SelectedItems.Count != 1 || EntryGrid.SelectedItem is not EntryView row) return Task.CompletedTask;
        return QueryAsync(AileArc.Core.ArchiveIndex.ParentOf(row.Path), 0, clearSearch: true, revealId: long.Parse(row.Row.Id, CultureInfo.InvariantCulture));
    }
    private void RowCopy_Click(object sender, RoutedEventArgs e) => CopySelectedPaths();
    internal void CopySelectedPaths()
    {
        if (IsBusy || !EntryGrid.IsEnabled || EntryGrid.SelectedItems.Count == 0) return;
        var selected = EntryGrid.SelectedItems.Cast<EntryView>().ToHashSet();
        try { CopyText(string.Join(Environment.NewLine, displayed.Where(selected.Contains).Select(r => r.Path))); StatusText.Text = Text["ClientPathCopied"]; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = Text["ClientClipboardBusy"]; }
    }

    private bool CanDrop(IDataObject data) => !closed && !IsBusy && !closingCheck && ((ClientApp)Application.Current).CanStart(this)
        && data.GetDataPresent(DataFormats.FileDrop, false);
    private void Workspace_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        bool accept = CanDrop(e.Data) && e.AllowedEffects.HasFlag(DragDropEffects.Copy);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        if (accept && DropHint.Visibility != Visibility.Visible) { DropHint.Visibility = Visibility.Visible; FadeIn(DropHint); }
    }
    private void Workspace_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;
    private async void Workspace_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (!CanDrop(e.Data) || !e.AllowedEffects.HasFlag(DragDropEffects.Copy) || e.Data.GetData(DataFormats.FileDrop,false) is not string[] paths) return;
        e.Effects = DragDropEffects.Copy;
        await OpenDroppedAsync(paths);
    }
    internal async Task OpenDroppedAsync(string[] paths)
    {
        if (paths.Length is 0 or > 32 || !await Task.Run(() => paths.All(p => Path.IsPathFullyQualified(p) && File.Exists(p))))
        { StatusText.Text = Text["ClientDropInvalid"]; return; }
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (closed || IsBusy || closingCheck || !((ClientApp)Application.Current).CanStart(this)) return;
            await ((ClientApp)Application.Current).OpenPathAsync(path, preferred: this);
        }
    }
}
