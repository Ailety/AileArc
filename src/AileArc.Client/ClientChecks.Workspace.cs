using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckWorkspaceAsync(ClientApp app, string root)
    {
        string zipPath = Path.Combine(root, "带前缀的文件菜单.bin");
        string longName = new('目', 240);
        using (var output = File.Create(zipPath))
        {
            output.Write("MZ"u8); output.Position = 8192;
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            for (int i=0; i<204; i++) zip.CreateEntry($"wide/A{i:D3}.txt");
            for (int i = 0; i < 215; i++) zip.CreateEntry($"分页/{i:D3}{longName}/note.txt");
            foreach (string name in new[] { "wide/config.yml", "docs/config.yml", "docs/deps/依赖.txt", "docs2/保留.txt" })
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(name); }
        }
        var window = app.CreateWindow(); window.Show();
        try
        {
            // Exercise a preference change on the live visual without changing Windows settings.
            window.ActivityDot.Visibility = Visibility.Visible;
            window.ApplyMotionPreference(true);
            Require(window.ActivityDot.HasAnimatedProperties, "active visible window pulses");
            window.ApplyMotionPreference(false);
            Require(!window.ActivityDot.HasAnimatedProperties && window.ActivityDot.IsVisible && window.ActivityDot.Opacity == 1,
                "reduced motion immediately switches the running indicator to a steady dot");
            window.WindowState = WindowState.Minimized;
            window.ApplyMotionPreference(true);
            Require(!window.ActivityDot.HasAnimatedProperties, "minimized window suspends the running clock");
            window.WindowState = WindowState.Normal;
            window.ApplyMotionPreference(true);
            Require(window.ActivityDot.HasAnimatedProperties, "restored window resumes a permitted animation");
            window.ActivityDot.Visibility = Visibility.Collapsed;
            window.ApplyMotionPreference(true);
            Require(!window.ActivityDot.HasAnimatedProperties, "idle indicator releases its clock");
            await window.OpenDroppedAsync([zipPath, root]);
            Require(window.OpenedPath == null, "mixed folder drop rejected as a whole");
            await window.OpenDroppedAsync([zipPath, zipPath]); Require(window.OpenedPath == zipPath, "drop opens the archive in the empty window");
            window.CompactList.IsChecked = true; window.CompactList.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Require(window.TestGrid.RowHeight == 30, "compact table keeps file selection usable");
            window.SearchBox.Text = "config"; await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
            var matches = window.TestGrid.Items.Cast<EntryView>().ToArray();
            Require(matches.Length == 2, "search finds both matching paths");
            window.TestGrid.SelectAll(); string? copied = null; window.CopyText = value => copied = value;
            Require(window.TestArchiveButton.IsEnabled, "archive test available with a partial selection");
            await window.TestCurrentArchiveAsync();
            Require(window.SearchBox.Text == "config" && window.TestGrid.SelectedItems.Count == 2 &&
                window.StatusText.Text == ClientApp.Text.Format("TestArchiveOk", 204 + 215 + 4),
                "whole-archive test preserves the active search and multiple selection");
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

            await window.GoToAddressAsync("分页");
            string[] VisibleIds() => window.TestGrid.Items.Cast<EntryView>().Select(v => v.Row.Id).ToArray();
            var firstPage = VisibleIds();
            Require(firstPage.Length < 200 && firstPage.Length > 0, "long Chinese paths exercise metadata-limited paging");
            window.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle();
            var secondPage = VisibleIds();
            window.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle();
            window.PreviousButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle();
            Require(VisibleIds().SequenceEqual(secondPage), "previous page restores the complete short page");
            var child = window.TestGrid.Items.Cast<EntryView>().Last();
            await window.GoToAddressAsync(child.Path);
            window.UpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle();
            Require((window.TestGrid.SelectedItem as EntryView)?.Row.Id == child.Row.Id && VisibleIds().SequenceEqual(secondPage),
                "going up reveals the child just left on its actual page");
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
