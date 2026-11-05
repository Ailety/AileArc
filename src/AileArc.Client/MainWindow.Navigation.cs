using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AileArc.Core;

namespace AileArc.Client;

public partial class MainWindow
{
    private (string Name, string Path)? renderedAddress;
    private void RefreshAddress()
    {
        if (renderedAddress == (archiveName, directory)) return;
        renderedAddress = (archiveName, directory);
        Crumbs.Children.Clear();
        void Segment(string name, string target)
        {
            var button = new Button { Content = name, Tag = target, Padding = new Thickness(5,0,5,0) };
            button.Click += async (_, _) => await QueryAsync(target, 0, true);
            Crumbs.Children.Add(button);
        }
        if (archive is null) return;
        Segment(archiveName, "");
        string current = "";
        foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : current + "/" + part;
            Crumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center });
            Segment(part, current);
        }
        var edit = new Button { Content = "✎", Padding = new Thickness(5,0,5,0), ToolTip = Text["ClientAddress"] + " (Alt+D)" };
        AutomationProperties.SetName(edit, Text["ClientAddress"]); edit.Click += (_, _) => EditAddress(); Crumbs.Children.Add(edit);
    }
    private void EditAddress()
    {
        if (archive is null || IsBusy) return;
        AddressInput.Text = "/" + directory; AddressInput.Visibility = Visibility.Visible;
        AddressInput.Focus(); AddressInput.SelectAll();
    }
    private void Address_MouseDown(object sender, MouseButtonEventArgs e) => EditAddress();
    private void Address_Blur(object sender, KeyboardFocusChangedEventArgs e) => AddressInput.Visibility = Visibility.Collapsed;
    private async void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; AddressInput.Visibility = Visibility.Collapsed; EntryGrid.Focus(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true; await GoToAddressAsync(AddressInput.Text);
    }
    internal async Task GoToAddressAsync(string address)
    {
        var target = address.Replace('\\','/').Trim('/');
        if (archive is null || IsBusy) return;
        if (target.Length > 0 && !archive.Index.ContainsDirectory(target))
        { StatusText.Text = Text["ClientUnknownDirectory"]; return; }
        AddressInput.Visibility = Visibility.Collapsed; await QueryAsync(target, 0, true); EntryGrid.Focus();
    }
    private async void Forward_Click(object sender, RoutedEventArgs e) => await QueryAsync(directory, 0, forward: true);
    private async void NavigationKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || archive is null || IsBusy) return;
        switch (e.SystemKey)
        {
            case Key.D: e.Handled = true; EditAddress(); break;
            case Key.Left: e.Handled = true; await QueryAsync(directory, 0, back: true); break;
            case Key.Right: e.Handled = true; await QueryAsync(directory, 0, forward: true); break;
            case Key.Up: e.Handled = true; await QueryAsync(ArchiveIndex.ParentOf(directory), 0, true); break;
        }
    }
    private void UpdateHeaders()
    {
        DataGridColumn[] columns = [NameColumn, SizeColumn, TypeColumn, PathColumn];
        string[] labels = ["Name", "ClientSize", "ClientType", "Path"];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i].SortDirection = i != view.Sort ? null : view.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            columns[i].Header = Text[labels[i]] + (i == view.Sort ? view.Descending ? " ↓" : " ↑" : "");
        }
    }
    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        int index = e.Column.SortMemberPath switch { "Name" => 0, "Size" => 1, "Type" => 2, "Path" => 3, _ => -1 };
        if (index >= 0) await SortByAsync(index);
    }
    internal Task SortByAsync(int index) => QueryAsync(directory, 0, sort: index, descending: view.Sort == index && !view.Descending);
}
