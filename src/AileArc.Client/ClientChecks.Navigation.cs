using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls.Primitives;

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
        }
        await window.TestOpenAsync(path);
        window.SearchBox.Text = "match";
        await Click(window.SearchSubmit);
        Require(window.PathColumn.Visibility == Visibility.Visible, "show archive paths in search");
        window.SearchBox.Text = "unsubmitted";
        await Click(window.NextButton);
        var rows = window.TestGrid.Items.Cast<EntryView>().ToArray();
        Require(rows.Length == 3 && rows.Where(r => r.Name == "MatchHidden.txt").Select(r => r.Path).Distinct().Count() == 2,
            "whole archive search, distinct result paths and paging use submitted query");
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
            // WPF's header peer does not expose Invoke on every theme/runtime. Exercise
            // the actual header Click implementation without injecting desktop input.
            typeof(DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(header, null);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (window.IsBusy && DateTime.UtcNow < limit) await Task.Delay(10);
        }
        await Header(window.SizeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name == "204.md", "size header sorts entire archive before pagination");
        await Header(window.SizeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name == "000.txt", "second click reverses size order");
        await Header(window.TypeColumn);
        Require(window.TestGrid.Items.Cast<EntryView>().First().Name == "204.md", "type header uses displayed type");
    }
}
