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
        await Click(window.EnterButton);
        Require(window.SearchBox.Text == "" && window.PathColumn.Visibility == Visibility.Collapsed &&
            window.TestGrid.Items.Cast<EntryView>().Single().Name == "内容.txt", "open found folder and exit search");
        await Click(window.BackButton);
        Require(window.SearchBox.Text == "match" && window.PreviousButton.IsEnabled &&
            (window.TestGrid.SelectedItem as EntryView)?.Row.Id == folder.Row.Id, "back restores result page and identity");
        window.LocalSearchBox.IsChecked = true; await Click(window.LocalSearchBox);
        await Click(window.NextButton);
        Require(window.TestGrid.Items.Cast<EntryView>().Single().Name == "Match200", "current directory excludes deeper names");
        await window.TestOpenAsync(path);
        Require(!window.BackButton.IsEnabled && window.LocalSearchBox.IsChecked == false, "archive replacement resets navigation");
    }
}
