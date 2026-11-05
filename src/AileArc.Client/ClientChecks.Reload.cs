using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AileArc.Core;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckReloadAsync(ClientApp app, string root)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(root, "旧编码.zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.GetEncoding(936)))
        {
            foreach (var (name, content) in new[] { ("中文/资料/match.txt", "first"), ("中文/资料/match.txt", "second"),
                ("中文/资料/match-2.txt", "other selected"), ("other/match.txt", "different folder"), ("其它/extra.txt", "separator changes in Shift-JIS") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content);
            }
        }
        var worker = new ArchiveWorkerClient(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
        var wrong = await worker.ScanAsync(path, nameCodePage: 932);
        string WrongParent(int id) => ArchiveIndex.ParentOf(wrong.Entries[id].Path.Replace('\\', '/'));
        Require(WrongParent(0) != "中文/资料", "fixture has genuinely different decoded paths");
        var window = app.CreateWindow(); window.Show();
        async Task Idle()
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (window.IsBusy && DateTime.UtcNow < until) await Task.Delay(10);
            Require(!window.IsBusy, "reload or navigation completed");
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void CheckContext()
        {
            Require(window.CurrentCodePage == 936 && window.SearchBox.Text == "match" && window.LocalSearchBox.IsChecked == true &&
                (window.Folders.SelectedItem as FolderNode)?.Path == "中文/资料",
                $"decoded context: cp={window.CurrentCodePage}, query={window.SearchBox.Text}, local={window.LocalSearchBox.IsChecked}, folder={(window.Folders.SelectedItem as FolderNode)?.Path}, status={window.StatusText.Text}");
            var selected = window.TestGrid.SelectedItems.Cast<EntryView>().Select(v => v.Row.Id).Order().ToArray();
            Require(selected.SequenceEqual(["1", "2"]) && window.TestGrid.Items.Count == 3,
                "restored selection distinguishes duplicate file names");
        }
        try
        {
            await window.OpenArchiveAsync(path, 932);
            await window.GoToAddressAsync(WrongParent(0));
            window.LocalSearchBox.IsChecked = true; window.SearchBox.Text = "match";
            await window.SearchSettled.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var row in window.TestGrid.Items.Cast<EntryView>().Where(v => v.Row.Id is "1" or "2").ToArray())
                window.TestGrid.SelectedItems.Add(row);
            await window.GoToAddressAsync(WrongParent(3));
            Click(window.BackButton); await Idle();
            Require(window.SearchBox.Text == "match" && (window.Folders.SelectedItem as FolderNode)?.Path == WrongParent(0), "history restored the search before changing encoding");
            window.EncodingBox.SelectedIndex = 2; await Idle();
            CheckContext();
            Click(window.ForwardButton); await Idle();
            Require((window.Folders.SelectedItem as FolderNode)?.Path == "other", "forward still reaches its folder after redecoding");
            Click(window.BackButton); await Idle(); CheckContext();
            Click(window.RefreshButton); await Idle(); CheckContext();
            string target = Path.Combine(root, "重读后所选");
            await window.ExtractToAsync(target, false, true);
            Require(File.ReadAllText(Path.Combine(target, "中文", "资料", "match.txt")) == "second" &&
                Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 2, "only reselected IDs extracted with corrected names");
            string? opened = null; window.LaunchFile = (file, _) => opened = File.ReadAllText(file);
            await window.OpenExternalAsync(1);
            Require(opened == "second", "default opener receives the correct corrected-name duplicate");

            await window.GoToAddressAsync("其它");
            window.EncodingBox.SelectedIndex = 3; await Idle();
            Require((window.Folders.SelectedItem as FolderNode)?.Path == "" && window.TestGrid.SelectedItems.Count == 0 &&
                window.StatusText.Text == ClientApp.Text["ClientReloadLayoutChanged"], "a vanished directory cannot redirect the selection");
            Click(window.BackButton); await Idle();
            window.EncodingBox.SelectedIndex = 2; await Idle(); CheckContext();

            File.Move(path, path + ".old");
            await window.RefreshArchiveAsync(); CheckContext();
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("new.txt");
            window.EncodingBox.SelectedIndex = 3; await Idle(); CheckContext();
            Require(window.StatusText.Text == ClientApp.Text["ClientReloadRequired"], "encoding switch cannot reuse IDs from a replacement");
            opened = null;
            await window.OpenExternalAsync(1);
            Require(opened is null && window.StatusText.Text == ClientApp.Text["ClientReloadRequired"], "cached copy cannot bypass the changed-source check");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.F5)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Idle();
            Require(window.CurrentCodePage == 0 && window.SearchBox.Text.Length == 0 && !window.BackButton.IsEnabled &&
                !window.ForwardButton.IsEnabled && window.TestGrid.SelectedItems.Count == 0 &&
                window.TestGrid.Items.Cast<EntryView>().Single().Name == "new.txt" &&
                window.StatusText.Text == ClientApp.Text["ClientRefreshedReplacement"], "F5 opens changed contents without stale state");
            window.SearchBox.Text = "new";
            await window.RefreshArchiveAsync();
            Require(window.SearchBox.Text == "new" && window.BackButton.IsEnabled && window.TestGrid.Items.Count == 1,
                "reload during search debounce keeps the typed term and back history");
        }
        finally { window.ApproveClose(); window.Close(); }
    }
}
