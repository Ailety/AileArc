using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckWorkspaceAsync(ClientApp app, string root)
    {
        string zipPath = Path.Combine(root, "文件菜单.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            for (int i=0; i<204; i++) zip.CreateEntry($"wide/A{i:D3}.txt");
            foreach (string name in new[] { "wide/config.yml", "docs/config.yml", "docs/deps/依赖.txt", "docs2/保留.txt" })
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(name); }
        }
        var window = app.CreateWindow(); window.Show();
        try
        {
            await window.OpenDroppedAsync([zipPath, root]);
            Require(window.OpenedPath == null, "mixed folder drop rejected as a whole");
            await window.OpenDroppedAsync([zipPath, zipPath]); Require(window.OpenedPath == zipPath, "drop opens the archive in the empty window");
            window.CompactList.IsChecked = true; window.CompactList.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Require(window.TestGrid.RowHeight == 30, "compact table keeps file selection usable");
            window.SearchBox.Text = "config"; await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
            var matches = window.TestGrid.Items.Cast<EntryView>().ToArray();
            Require(matches.Length == 2, "search finds both matching paths");
            window.TestGrid.SelectAll(); string? copied = null; window.CopyText = value => copied = value;
            window.CopySelectedPaths();
            Require(copied == string.Join(Environment.NewLine, matches.Select(r => r.Path)), "multiselect copies visible paths in list order");
            window.TestGrid.UnselectAll();
            var late = matches.Single(r => r.Path == "wide/config.yml"); window.TestGrid.SelectedItem = late;
            await window.LocateSelectedAsync();
            Require(window.SearchBox.Text == "" && window.PreviousButton.IsEnabled && (window.TestGrid.SelectedItem as EntryView)?.Row.Id == late.Row.Id, "locate selects the exact entry beyond first page");
            window.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitIdle();
            Require(window.SearchBox.Text == "config" && (window.TestGrid.SelectedItem as EntryView)?.Row.Id == late.Row.Id, "back restores search and selection after locate");
            window.TestGrid.SelectedItem = window.TestGrid.Items.Cast<EntryView>().Single(r => r.Path == "docs/config.yml");
            string destination = Path.Combine(root,"所在目录");
            await ObserveDialogAsync(app, ClientApp.Text["Extract"], () =>
            {
                window.RowParentExtract.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return Task.CompletedTask;
            }, dialog =>
            {
                Require(dialog.Body.Children.OfType<TextBlock>().Any(t => t.Text.Contains("docs")), "parent scope named before writing");
                dialog.Body.Children.OfType<TextBox>().Single().Text = destination;
            }, accept:true);
            await WaitIdle();
            Require(File.ReadAllText(Path.Combine(destination,"docs","deps","依赖.txt")) == "docs/deps/依赖.txt" &&
                Directory.EnumerateFiles(destination,"*",SearchOption.AllDirectories).Count() == 2, "folder extraction includes dependencies and excludes siblings");
        }
        finally { window.ApproveClose(); window.Close(); }
        async Task WaitIdle()
        {
            var until=DateTime.UtcNow.AddSeconds(15);
            while (window.IsBusy && DateTime.UtcNow < until) await Task.Delay(10);
            Require(!window.IsBusy, "workspace action finished");
        }
    }
}
