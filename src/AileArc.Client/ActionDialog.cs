using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AileArc.Client;

internal sealed class ActionDialog : Window
{
    internal StackPanel Body { get; } = new() { Margin = new Thickness(24) };
    internal Button Accept { get; }
    internal ActionDialog(Window owner, string title, string accept = "Continue", string cancel = "Cancel")
    {
        Owner = owner; Title = ClientApp.Text[title]; Width = 560; MaxHeight = 700;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("WarmGraphite.xaml", UriKind.Relative) });
        Background = (Brush)FindResource("SurfaceBrush"); Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 14;
        var layout = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 0, 24, 20) };
        Accept = new Button { Content = ClientApp.Text[accept], Style = (Style)FindResource("Primary") };
        Accept.Click += (_, _) => DialogResult = true;
        actions.Children.Add(new Button { Content = ClientApp.Text[cancel], IsCancel = true }); actions.Children.Add(Accept);
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = layout;
    }
    internal void Explain(string text) => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
    internal ComboBox Choices(string label, string[] items)
    {
        Explain(label);
        var box = new ComboBox { ItemsSource = items, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(box, label); Body.Children.Add(box); return box;
    }
}
