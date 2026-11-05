using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace AileArc.Client;

internal static class GraphiteFrame
{
    public static void Install(Window window, Button? action = null)
    {
        bool main = action is not null;
        int height = main ? 48 : 38;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = height, GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(main ? 6 : 0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        var body = (UIElement)window.Content; window.Content = null;
        var shell = new DockPanel();
        var bar = new DockPanel { Height = height, Background = (Brush)window.FindResource("SurfaceBrush") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        WindowChrome.SetIsHitTestVisibleInChrome(buttons,true);
        if (action != null) buttons.Children.Add(action);
        Button Control(string glyph, string label, Action execute)
        {
            var button = new Button { Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), Width = 42, Height = height - 8, Padding = new Thickness(0), ToolTip = ClientApp.Text[label] };
            AutomationProperties.SetName(button, ClientApp.Text[label]); button.Click += (_, _) => execute(); buttons.Children.Add(button); return button;
        }
        if (main)
        {
            Control("\uE921", "ClientMinimize", () => SystemCommands.MinimizeWindow(window));
            var max = Control("\uE922", "ClientMaximize", () => { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); });
            window.StateChanged += (_, _) => {
                bool large = window.WindowState == WindowState.Maximized;
                max.Content = large ? "\uE923" : "\uE922"; max.ToolTip = ClientApp.Text[large ? "ClientRestore" : "ClientMaximize"];
                AutomationProperties.SetName(max, (string)max.ToolTip);
            };
        }
        Control("\uE8BB", "Close", window.Close);
        DockPanel.SetDock(buttons,Dock.Right); bar.Children.Add(buttons);
        bar.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Brand/mark.png")), Width = 30, Height = 30, Margin = new Thickness(14,0,8,0) });
        var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0,0,12,0), FontWeight = FontWeights.SemiBold };
        caption.SetBinding(TextBlock.TextProperty,new Binding("Title") { Source = window }); bar.Children.Add(caption);
        DockPanel.SetDock(bar,Dock.Top); shell.Children.Add(bar); shell.Children.Add(body); window.Content = shell;
    }
}
