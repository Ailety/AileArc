using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AileArc.Client;

internal sealed class PasswordDialog : Window
{
    private readonly PasswordBox input = new() { MaxLength = 4096, Padding = new Thickness(10), FontSize = 16 };
    public string Value => input.Password;
    public PasswordDialog(MainWindow owner, string error)
    {
        Owner = owner; Title = ClientApp.Text["Password"]; Width = 440; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("WarmGraphite.xaml", UriKind.Relative) });
        Background = (Brush)FindResource("SurfaceBrush"); Foreground = (Brush)FindResource("TextBrush");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = ClientApp.Text[error], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,16) });
        input.Background = (Brush)FindResource("CanvasBrush"); input.Foreground = Foreground; input.BorderBrush = (Brush)FindResource("AccentBrush");
        System.Windows.Automation.AutomationProperties.SetName(input, ClientApp.Text["Password"]);
        content.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        var cancel = new Button { Content = ClientApp.Text["Cancel"], IsCancel = true };
        var accept = new Button { Content = ClientApp.Text["Continue"], IsDefault = true, Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(accept); content.Children.Add(actions); Content = content;
        Loaded += (_, _) => input.Focus();
    }
    public void Clear() => input.Clear();
}
