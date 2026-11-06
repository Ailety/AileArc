using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckFoldersAsync(MainWindow window, string root)
    {
        string zipPath = Path.Combine(root, "目录树.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            for (int i = 0; i < 110; i++) zip.CreateEntry($"R{i:D3}/file.txt");
            zip.CreateEntry("甲/同名/深层/甲.txt"); zip.CreateEntry("乙/同名/深层/乙.txt");
            zip.CreateEntry("甲/空目录/");
        }
        await window.TestOpenAsync(zipPath);
        var folderList = window.Folders;
        folderList.IsEnabled = false;
        folderList.ApplyTemplate();
        Require(VisualTreeHelper.GetChild(folderList, 0) is Border { Background: SolidColorBrush { Color.A: 0 } } &&
            folderList.Template.FindName("PART_ScrollViewer", folderList) is ScrollViewer { CanContentScroll: true },
            "disabled tree keeps its graphite surface and item-scrolling template");
        folderList.IsEnabled = true;
        FolderNode Node(string path) => window.Folders.Items.Cast<FolderNode>().Single(n => n.Path == path);
        bool Visible(string path) => window.Folders.Items.Cast<FolderNode>().Any(n => n.Path == path);
        string? Current() => (window.Folders.SelectedItem as FolderNode)?.Path;
        async Task Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (window.IsBusy && DateTime.UtcNow < until) await Task.Delay(10);
            Require(!window.IsBusy, "tree navigation completes");
        }
        async Task<Button[]> Buttons(FolderNode node)
        {
            window.Folders.ScrollIntoView(node);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            static IEnumerable<Button> Descendants(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is Button b) yield return b;
                    else foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            var container = window.Folders.ItemContainerGenerator.ContainerFromItem(node);
            Require(container != null, "virtualized folder row realized");
            return Descendants(container!).ToArray();
        }
        Require(Current() == "" && Visible("R109") && !Visible("甲/同名"), "root selected, no 100-folder cutoff");
        await Click((await Buttons(Node("甲"))).Single(b => Equals(b.Content,"▸")));
        Require(Current() == "" && Visible("甲/空目录") && !Node("甲/空目录").HasChildren, "disclose without navigation, explicit empty leaf");
        await Click((await Buttons(Node("甲/同名"))).Single(b => b.Content is DockPanel));
        Require(Current() == "甲/同名" && Visible("甲/同名/深层") && window.Crumbs.Children.OfType<Button>().Any(b => Equals(b.Tag,"甲/同名")), "tree click updates breadcrumb and expands current branch");
        window.TestGrid.SelectedItem = window.TestGrid.Items.Cast<EntryView>().Single(r => r.Name == "深层");
        await window.ActivateAsync();
        Require(Current() == "甲/同名/深层" && window.TestGrid.Items.Cast<EntryView>().Single().Name == "甲.txt", "list activation updates tree");
        await window.GoToAddressAsync("乙/同名/深层");
        Require(Current() == "乙/同名/深层" && Node("乙").Expanded && Node("乙/同名").Expanded, "address reveals correct ancestors of duplicate leaf names");
        await Click(window.BackButton); Require(Current() == "甲/同名/深层", "back reveals previous tree path");
        await Click(window.ForwardButton); Require(Current() == "乙/同名/深层", "forward reveals next tree path");
        await Click((await Buttons(Node("乙"))).Single(b => Equals(b.Content,"▾")));
        Require(!Visible("乙/同名") && window.TestGrid.Items.Cast<EntryView>().Single().Name == "乙.txt", "collapse leaves current list intact");
        await Click((await Buttons(Node("乙"))).Single(b => Equals(b.Content,"▸")));
        Require(Current() == "乙/同名/深层", "reopening manually folded ancestor restores highlight");
        window.ToggleFolder(Node("乙")); await window.GoToAddressAsync("/乙/同名/深层");
        Require(Current() == "乙/同名/深层" && Node("乙").Expanded, "explicitly revisiting a hidden current address reveals it");
        await window.TestOpenAsync(zipPath);
        Require(Current() == "" && !Visible("甲/同名"), "new session starts with fresh branches");
    }
}
