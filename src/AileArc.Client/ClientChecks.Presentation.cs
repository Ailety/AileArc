using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckWorkspaceStatesAsync(ClientApp app, string root, string? outputDirectory)
    {
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "Design handoff.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "assets/icons.svg", "docs/制作说明.md", "src/config.yml", "CHANGELOG.md", "manifest.json", "README.md" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        var window = app.CreateWindow();
        window.Show();
        try
        {
            async Task Layout() => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            async Task Capture(string scene)
            {
                await Layout();
                if (outputDirectory is null) return;
                Directory.CreateDirectory(outputDirectory);
                RenderViewport(window, 96, outputDirectory, scene);
            }
            await Capture("home");
            Require(window.HomeOpen.IsVisible && window.HomeCreate.IsVisible && !window.ArchiveBody.IsVisible &&
                !window.Folders.IsVisible && !window.DetailPane.IsVisible, "home offers two file actions without empty browser controls");
            window.Width = 680; await Layout();
            window.HomeTools.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.LanguageBox.IsVisible, "compact home can reach language and recovery");
            window.HideFolders.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Width = 1250;
            await window.TestOpenAsync(archive); await Capture("archive");
            Require(!window.HomePanel.IsVisible && window.EntryGrid.IsVisible && window.OverviewPath.Text == archive &&
                window.OverviewSize.Text == EntryView.FormatSize(new FileInfo(archive).Length), "archive inspector uses the on-disk archive size and path");
            string? opened = null;
            window.OpenFolder = path => opened = path;
            window.ArchiveFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(opened == root, "open containing folder targets the archive's parent");
            var files = window.EntryGrid.Items.Cast<EntryView>().Where(item => item.Row.Type != "folder").Take(2).ToArray();
            foreach (var file in files) window.EntryGrid.SelectedItems.Add(file);
            Require(window.SummaryText.Text == ClientApp.Text.Format("ClientSelectedFileSize", 2,
                EntryView.FormatSize(files.Sum(file => (double)file.Row.UnpackedBytes!.Value))), "selection summary totals the two selected files");
            window.EntryGrid.UnselectAll(); window.EntryGrid.SelectedItem = files[0];
            string? copied = null; window.CopyText = value => copied = value;
            window.CopyPathButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(copied == files[0].Path && Equals(window.CopyPathButton.Content, ClientApp.Text["ClientCopied"]), "copy feedback appears next to the action");
            window.EntryGrid.SelectedItem = files[1];
            Require(Equals(window.CopyPathButton.Content, ClientApp.Text["ClientCopyPath"]), "copy feedback resets for a different file");

            NavigationCommands.GoToPage.Execute(null, window);
            Require(window.AddressInput.IsVisible, "address shortcut enters editable mode");
            await window.GoToAddressAsync("docs");
            async Task SideButton(MouseButton button)
            {
                var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                window.RaiseEvent(e);
                Require(e.Handled, "mouse history navigation consumes only an available step");
                await window.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await SideButton(MouseButton.XButton1);
            Require(window.EntryGrid.Items.Count == 6, "mouse back restores archive root");
            await SideButton(MouseButton.XButton2);
            Require(window.EntryGrid.Items.Cast<EntryView>().Single().Path == "docs/制作说明.md", "mouse forward restores directory");
        }
        finally { window.ApproveClose(); window.Close(); }
    }
}
