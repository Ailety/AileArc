using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AileArc.Client;

public partial class MainWindow
{
    private ArchiveFolderTree? folderTree;
    private FolderNode? currentFolder;
    private bool syncingTree;

    private void SyncFolderTree(bool reveal = false)
    {
        if (folderTree is null) return;
        var next = folderTree[directory];
        if (!reveal && ReferenceEquals(next, currentFolder)) return; // Search, sort and paging preserve manual folding.
        bool rebuild = currentFolder is null;
        currentFolder = next;
        for (FolderNode? n = next; n is not null; n = n.Parent)
            if (n.HasChildren && !n.Expanded) { n.Expanded = true; rebuild = true; }
        ReflectFolder(rebuild, next);
    }

    private void ReflectFolder(bool rebuild, FolderNode? reveal)
    {
        if (folderTree is null) return;
        syncingTree = true;
        try
        {
            if (rebuild) Folders.ItemsSource = folderTree.VisibleRows();
            Folders.SelectedItem = currentFolder != null && Folders.Items.Contains(currentFolder) ? currentFolder : null;
            if (reveal != null) Folders.ScrollIntoView(reveal);
        }
        finally { syncingTree = false; }
    }

    internal void ToggleFolder(FolderNode node)
    {
        if (IsBusy || !node.HasChildren) return;
        node.Expanded = !node.Expanded;
        ReflectFolder(true, node);
    }
    private void ToggleFolder_Click(object sender, RoutedEventArgs e)
    { if (((FrameworkElement)sender).DataContext is FolderNode node) ToggleFolder(node); }

    private async Task EnterFolderAsync(FolderNode node)
    {
        if (IsBusy) return;
        if (node.Path != directory || view.Search.Length > 0 || SearchBox.Text.Length > 0)
            await QueryAsync(node.Path, 0, clearSearch: true);
        ReflectFolder(false, currentFolder); // If navigation was cancelled, restore the committed selection.
        if (treeOverlayOpen && node.Path == directory) { CloseOverlay(false); EntryGrid.Focus(); }
    }
    private async void Folder_Click(object sender, RoutedEventArgs e)
    { if (((FrameworkElement)sender).DataContext is FolderNode node) await EnterFolderAsync(node); }
    private async void TreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!syncingTree && Folders.SelectedItem is FolderNode node) await EnterFolderAsync(node); }
    private async void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || Folders.SelectedItem is not FolderNode node) return;
        if (e.Key == Key.Enter) { e.Handled = true; await EnterFolderAsync(node); }
        else if (e.Key == Key.Right)
        {
            e.Handled = true;
            if (node.HasChildren && !node.Expanded) ToggleFolder(node);
            else if (node.HasChildren) await EnterFolderAsync(node.Children[0]);
        }
        else if (e.Key == Key.Left)
        {
            e.Handled = true;
            if (node.Expanded) ToggleFolder(node);
            else if (node.Parent is not null) await EnterFolderAsync(node.Parent);
        }
    }
}
