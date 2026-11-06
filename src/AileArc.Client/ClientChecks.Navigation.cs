using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckSearchHistoryAsync(MainWindow window, string root)
    {
        var path = Path.Combine(root, "搜索导航.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (int i = 0; i <= 200; i++) zip.CreateEntry($"Match{i:D3}/内容.txt");
            zip.CreateEntry("甲/MatchHidden.txt"); zip.CreateEntry("乙/MatchHidden.txt");
        }
        async Task Click(ButtonBase control)
        {
            Require(control.IsEnabled, "navigation control enabled");
            control.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (window.IsBusy && DateTime.UtcNow < limit) await Task.Delay(10);
            Require(!window.IsBusy, "navigation completed");
            await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await window.TestOpenAsync(path);
        window.SearchBox.Text = "obsolete";
        window.TestGrid.Focus();
        var find = window.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.F && k.Modifiers == ModifierKeys.Control);
        ((RoutedCommand)find.Command).Execute(null, window);
        Require(FocusManager.GetFocusedElement(window) == window.SearchBox && window.SearchBox.SelectedText == "obsolete", "Ctrl+F focuses search and selects its content");
        void Composition(RoutedEvent stage)
        {
            var composition = new TextComposition(InputManager.Current, window.SearchBox, "");
            window.SearchBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = stage, Handled = true });
        }
        Composition(TextCompositionManager.PreviewTextInputStartEvent);
        window.SearchBox.Text = "nei";
        Composition(TextCompositionManager.PreviewTextInputUpdateEvent);
        await Task.Delay(300);
        Require(window.TestGrid.Items.Count == 200 && window.PathColumn.Visibility == Visibility.Collapsed, "IME preedit never filters even after debounce");
        window.SearchBox.Text = "内容";
        Composition(TextCompositionManager.PreviewTextInputEvent);
        await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Require(window.TestGrid.Items.Cast<EntryView>().All(r => r.Name == "内容.txt") && window.TestGrid.Items.Count == 200, "IME commit filters the final Chinese word");
        await Click(window.ClearSearch);
        Require(window.SearchBox.Text == "" && window.PathColumn.Visibility == Visibility.Collapsed && window.TestGrid.Items.Count == 200, "one-click clear restores directory");
        window.SearchBox.Text = "MatchH";
        await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
        window.SearchBox.Text = "matchhidd";
        window.SearchBox.Text = "invalid";
        window.SearchBox.Text = "match";
        await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Require(window.PathColumn.Visibility == Visibility.Visible, "show archive paths in search");
        await Click(window.BackButton);
        Require(window.SearchBox.Text == "" && window.PathColumn.Visibility == Visibility.Collapsed, "back skips intermediate search prefixes");
        await Click(window.ForwardButton);
        Require(window.SearchBox.Text == "match", "forward restores latest search");
        await Click(window.NextButton);
        var rows = window.TestGrid.Items.Cast<EntryView>().ToArray();
        Require(rows.Length == 3 && rows.Where(r => r.Name == "MatchHidden.txt").Select(r => r.Path).Distinct().Count() == 2,
            "latest live search, distinct result paths and paging");
        var folder = rows.Single(r => r.Name == "Match200");
        window.TestGrid.SelectedItem = folder;
        await window.ActivateAsync();
        Require(window.SearchBox.Text == "" && window.PathColumn.Visibility == Visibility.Collapsed &&
            window.TestGrid.Items.Cast<EntryView>().Single().Name == "内容.txt", "open found folder and exit search");
        await Click(window.BackButton);
        Require(window.SearchBox.Text == "match" && window.PreviousButton.IsEnabled &&
            (window.TestGrid.SelectedItem as EntryView)?.Row.Id == folder.Row.Id, "back restores result page and identity");
        await Click(window.ForwardButton);
        Require(window.TestGrid.Items.Cast<EntryView>().Single().Name == "内容.txt", "forward re-enters result folder");
        await Click(window.BackButton);
        window.LocalSearchBox.IsChecked = true; await Click(window.LocalSearchBox);
        await Click(window.NextButton);
        Require(window.TestGrid.Items.Cast<EntryView>().Single().Name == "Match200", "current directory excludes deeper names");
        await window.TestOpenAsync(path);
        Require(!window.BackButton.IsEnabled && window.LocalSearchBox.IsChecked == false, "archive replacement resets navigation");
        await window.GoToAddressAsync("/Match120");
        Require(window.TestGrid.Items.Cast<EntryView>().Single().Path == "Match120/内容.txt", "address resolves an implicit folder");
        window.TestGrid.SelectedIndex = 0;
        Require(window.DetailPath.Text == "Match120/内容.txt" && window.DetailSize.Text == ClientApp.Text.Format("ClientExactSize", 0), "selected nested file shows its path and exact length");
        var selectedFile = window.TestGrid.SelectedItem;
        if (!window.DetailPane.IsVisible) await Click(window.ShowDetails);
        await Click(window.HideDetails); window.UpdateLayout();
        Require(window.DetailColumn.ActualWidth == 0 && window.ShowDetails.IsVisible, "hidden inspector gives its width to the list");
        await Click(window.ShowDetails); window.UpdateLayout();
        Require(window.DetailPane.IsVisible && (window.BrowserBody.ActualWidth < 1120 || window.DetailColumn.ActualWidth > 0) &&
            window.TestGrid.SelectedItem == selectedFile, "reopened inspector retains selection in docked and narrow windows");
        string? copied = null, launched = null;
        var realCopy = window.CopyText; var previousLauncher = window.LaunchFile;
        try
        {
            window.CopyText = value => copied = value;
            await Click(window.CopyPathButton);
            Require(copied == "Match120/内容.txt", "copy uses internal path without filesystem prefix");
            window.LaunchFile = (file, _) => launched = file;
            await Click(window.OpenFileButton);
            Require(launched == window.LastCopyPath && File.Exists(launched), "inspector sends a real prepared copy to the default-app launcher");
        }
        finally { window.CopyText = realCopy; window.LaunchFile = previousLauncher; }
        await window.GoToAddressAsync("/missing");
        Require(window.TestGrid.Items.Cast<EntryView>().Single().Path == "Match120/内容.txt", "unknown address retains location");
        await Click(window.BackButton); await window.GoToAddressAsync("/Match042");
        Require(!window.ForwardButton.IsEnabled, "new branch discards forward history");
        string sortable = Path.Combine(root, "排序.zip");
        using (var zip = ZipFile.Open(sortable, ZipArchiveMode.Create))
            for (int i = 0; i < 205; i++)
            {
                using var stream = zip.CreateEntry(i == 204 ? "204.md" : $"{i:D3}.txt").Open();
                stream.Write(new byte[205-i]);
            }
        await window.TestOpenAsync(sortable);
        window.ToggleAll();
        Require(window.TestGrid.SelectedItems.Count == 200 && window.TestAllState == true, "select-all matches the real first page");
        await Click(window.NextButton);
        Require(window.TestGrid.SelectedItems.Count == 0 && window.TestGrid.Items.Count == 5, "next page excludes the previous selection");
        window.ToggleAll();
        Require(window.TestGrid.SelectedItems.Count == 5 && window.TestAllState == true, "select-all covers the last short page");
        window.ToggleAll();
        Require(window.TestGrid.SelectedItems.Count == 0 && window.TestAllState == false, "select-all toggle clears the page");
        await Click(window.PreviousButton);
        async Task Header(System.Windows.Controls.DataGridColumn column)
        {
            window.UpdateLayout();
            static IEnumerable<DataGridColumnHeader> Headers(DependencyObject parent)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                    if (child is DataGridColumnHeader h) yield return h;
                    else foreach (var nested in Headers(child)) yield return nested;
                }
            }
            var header = Headers(window.TestGrid).Single(h => h.Column == column);
            var mark = (FrameworkElement)header.Template.FindName("SortMark", header);
            var bounds = mark.RenderSize;
            // WPF's header peer does not expose Invoke on every theme/runtime. Exercise
            // the actual header Click implementation without injecting desktop input.
            typeof(DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(header, null);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (window.IsBusy && DateTime.UtcNow < limit) await Task.Delay(10);
            window.UpdateLayout();
            var up = (System.Windows.Shapes.Path)header.Template.FindName("UpMark", header);
            var down = (System.Windows.Shapes.Path)header.Template.FindName("DownMark", header);
            bool ascending = header.SortDirection == System.ComponentModel.ListSortDirection.Ascending;
            Require(bounds.Width > 0 && bounds.Height > 0 && mark.RenderSize == bounds &&
                up.Data.Bounds.Size == down.Data.Bounds.Size && up.StrokeThickness == down.StrokeThickness &&
                (ascending ? up : down).Opacity > (ascending ? down : up).Opacity &&
                System.Windows.Automation.AutomationProperties.GetItemStatus(header) == ClientApp.Text[ascending ? "ClientAscending" : "Descending"],
                "header uses matching chevrons and exposes the committed sort direction");
        }
        await Header(window.SizeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name == "204.md", "size header sorts entire archive before pagination");
        await Header(window.SizeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name == "000.txt", "second click reverses size order");
        await Header(window.TypeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name == "204.md", "type header uses displayed type");
        Require(window.DetailProperties.Visibility == Visibility.Collapsed, "sorting clears stale detail properties");
        window.TestGrid.SelectedIndex = 0;
        Require(window.DetailSize.Text == ClientApp.Text.Format("ClientExactSize", 1), "one byte is not rounded to zero");
        await Header(window.TypeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name.EndsWith(".txt"), "type header reverses its order");
    }
}
