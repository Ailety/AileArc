using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AileArc.Client;

public partial class MainWindow
{
    private bool dockedDetails = true;
    private bool compactDetails, compactTree, shortWindow, toolsOpen;
    private bool treeOverlayOpen, detailOverlayOpen;

    private void BrowserBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool details = e.NewSize.Width < 1120, tree = e.NewSize.Width < 840, low = e.NewSize.Height < 550;
        if (low) SideCommands.MaxHeight = Math.Max(100, e.NewSize.Height - 160);
        if (details == compactDetails && tree == compactTree && low == shortWindow) return;
        if (details != compactDetails) detailOverlayOpen = false;
        if (tree != compactTree) treeOverlayOpen = false;
        if (low != shortWindow) toolsOpen = false;
        compactDetails = details; compactTree = tree; shortWindow = low;
        ApplyPaneLayout();
    }

    private void ApplyPaneLayout()
    {
        bool browsing = archive is not null;
        bool showDetails = browsing && (compactDetails ? detailOverlayOpen : dockedDetails);
        var browserVisibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        ArchiveBody.Visibility = FolderArea.Visibility = ToolsSeparator.Visibility = browserVisibility;
        TestArchiveButton.Visibility = CreateButton.Visibility = OpenButton.Visibility = browserVisibility;
        HomePanel.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        HomeTools.Visibility = !browsing && compactTree ? Visibility.Visible : Visibility.Collapsed;
        CompactList.Visibility = browserVisibility;
        SideHeading.Text = Text[browsing ? "ClientArchive" : "ClientToolsAndSettings"];
        DockPanel.SetDock(ToolsGroup, browsing ? Dock.Bottom : Dock.Top);
        TreeColumn.Width = new GridLength(compactTree ? 0 : 218);
        TreeDivider.Width = new GridLength(compactTree ? 0 : 1);
        DetailColumn.Width = new GridLength(!compactDetails && showDetails ? 254 : 0);
        DetailDivider.Width = new GridLength(!compactDetails && showDetails ? 1 : 0);
        Grid.SetColumn(TreePane, compactTree ? 2 : 0);
        TreePane.HorizontalAlignment = compactTree ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        TreePane.Width = compactTree ? 218 : double.NaN;
        TreePane.Visibility = !compactTree || treeOverlayOpen ? Visibility.Visible : Visibility.Collapsed;
        ShowFolders.Visibility = HideFolders.Visibility = compactTree ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(DetailPane, compactDetails ? 2 : 4);
        DetailPane.HorizontalAlignment = compactDetails ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DetailPane.Width = compactDetails ? 254 : double.NaN;
        DetailPane.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        KeyboardNavigation.SetTabNavigation(TreePane, treeOverlayOpen ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetTabNavigation(DetailPane, detailOverlayOpen ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
        ShowDetails.Visibility = showDetails ? Visibility.Collapsed : Visibility.Visible;
        ToggleTools.Visibility = shortWindow && browsing ? Visibility.Visible : Visibility.Collapsed;
        NavigationRow.Height = new GridLength(shortWindow ? 44 : 65);
        SearchRow.Height = new GridLength(shortWindow ? 38 : 52);
        PagerRow.Height = new GridLength(shortWindow ? 36 : 46);
        NavigationBar.Margin = shortWindow ? new Thickness(12, 4, 12, 3) : new Thickness(18, 12, 18, 12);
        SearchBar.Margin = shortWindow ? new Thickness(12, 0, 12, 4) : new Thickness(18, 0, 18, 14);
        ToolsChevron.Text = toolsOpen ? "▾" : "▸";
        SideCommands.Visibility = !shortWindow || toolsOpen || !browsing ? Visibility.Visible : Visibility.Collapsed;
        if (!shortWindow) SideCommands.MaxHeight = double.PositiveInfinity;
    }

    private void ToggleFolders_Click(object sender, RoutedEventArgs e)
    {
        treeOverlayOpen = !treeOverlayOpen; detailOverlayOpen = false;
        ApplyPaneLayout();
        if (treeOverlayOpen) { FadeIn(TreePane); HideFolders.Focus(); }
        else (archive is null ? HomeTools : ShowFolders).Focus();
    }
    private void ToggleTools_Click(object sender, RoutedEventArgs e)
    {
        toolsOpen = !toolsOpen;
        ApplyPaneLayout();
    }
    private void OverlayKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || composing || (!treeOverlayOpen && !detailOverlayOpen)) return;
        e.Handled = true;
        CloseOverlay(true);
    }
    private void CloseOverlay(bool focusButton)
    {
        bool tree = treeOverlayOpen;
        treeOverlayOpen = detailOverlayOpen = false;
        ApplyPaneLayout();
        if (focusButton) (tree ? (archive is null ? HomeTools : ShowFolders) : ShowDetails).Focus();
    }
}
