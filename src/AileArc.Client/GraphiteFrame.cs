using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace AileArc.Client;

internal static class GraphiteFrame
{
    private const int MainCaptionHeight = 40;
    private const int ResizeBorder = 6;
    public static void Install(Window window, Button? action = null)
    {
        MonitorWorkArea.Install(window);
        bool main = action is not null;
        int height = main ? MainCaptionHeight : 36;
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("FrameStyles.xaml", UriKind.Relative) });
        // With zero glass WPF installs its own rectangular HRGN, which prevents DWM rounding.
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = height, GlassFrameThickness = new Thickness(0,0,0,1),
            ResizeBorderThickness = new Thickness(main ? ResizeBorder : 0), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
        var body = (UIElement)window.Content; window.Content = null;
        var shell = new DockPanel();
        var bar = new DockPanel { Height = height, Background = (Brush)window.FindResource("SurfaceBrush") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        if (action != null)
        {
            action.Margin = new Thickness(0, 0, 16, 0);
            action.VerticalAlignment = VerticalAlignment.Center;
            WindowChrome.SetIsHitTestVisibleInChrome(action, true);
            buttons.Children.Add(action);
        }
        Button Control(string glyph, string label, Action execute)
        {
            var button = new Button { Content = glyph, Style = (Style)window.FindResource(label == "Close" ? "WindowClose" : "WindowCommand"), ToolTip = ClientApp.Text[label] };
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            AutomationProperties.SetName(button, ClientApp.Text[label]); button.Click += (_, _) => execute(); buttons.Children.Add(button); return button;
        }
        Button? max = null;
        if (main)
        {
            Control("\uE921", "ClientMinimize", () => SystemCommands.MinimizeWindow(window));
            max = Control("\uE922", "ClientMaximize", () => { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); });
            window.StateChanged += (_, _) => {
                bool large = window.WindowState == WindowState.Maximized;
                max.Content = large ? "\uE923" : "\uE922"; max.ToolTip = ClientApp.Text[large ? "ClientRestore" : "ClientMaximize"];
                AutomationProperties.SetName(max, (string)max.ToolTip);
            };
        }
        Control("\uE8BB", "Close", window.Close);
        DockPanel.SetDock(buttons,Dock.Right); bar.Children.Add(buttons);
        bar.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Brand/mark.png")), Width = 22, Height = 22, Margin = new Thickness(16,0,8,0) });
        var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0,0,12,0), FontSize = 13, FontWeight = FontWeights.SemiBold };
        caption.SetBinding(TextBlock.TextProperty,new Binding("Title") { Source = window }); bar.Children.Add(caption);
        DockPanel.SetDock(bar,Dock.Top); shell.Children.Add(bar); shell.Children.Add(body); window.Content = shell;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int preference = 2; // DWMWCP_ROUND; the system chooses square corners when maximized or snapped.
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            int darkFrame = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 20, ref darkFrame, sizeof(int));
            if (max is not null) InstallMaximizeHitTest(window, hwnd, max);
        };
    }

    private static void InstallMaximizeHitTest(Window window, IntPtr hwnd, Button button)
    {
        const int hitMaxButton = 9, hitRight = 11, hitTop = 12, hitTopRight = 14;
        var source = HwndSource.FromHwnd(hwnd)!;
        HwndSourceHook hook = Hook;
        source.AddHook(hook);
        window.Closed += (_, _) => source.RemoveHook(hook);
        IntPtr Hook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (message)
            {
                case 0x0083 when IsZoomed(handle): // WM_NCCALCSIZE
                    // USER32 maximizes the resize frame just beyond the monitor. Keep the WPF
                    // content inside the work area so controls at all four edges stay reachable.
                    handled = MonitorWorkArea.ClipMaximizedClient(lParam);
                    break;
                case 0x0084 when button.IsVisible && button.IsEnabled: // WM_NCHITTEST, signed physical screen coordinates
                    long packed = lParam.ToInt64();
                    var screen = new Point((short)packed, (short)(packed >> 16));
                    var inWindow = window.PointFromScreen(screen);
                    // The caption buttons would otherwise claim the top and right resize edges.
                    if (window.WindowState == WindowState.Normal && inWindow.Y < MainCaptionHeight && inWindow.X > ResizeBorder)
                    {
                        bool top = inWindow.Y < ResizeBorder, right = inWindow.X >= window.ActualWidth - ResizeBorder;
                        if (top || right)
                        {
                            handled = true;
                            if (top && right) return hitTopRight;
                            return top ? hitTop : hitRight;
                        }
                    }
                    var local = button.PointFromScreen(screen);
                    if (local.X >= 0 && local.X < button.ActualWidth && local.Y >= 0 && local.Y < button.ActualHeight)
                    { handled = true; return hitMaxButton; }
                    break;
                case 0x00A0 when wParam == hitMaxButton: // WM_NCMOUSEMOVE
                    if (button.Tag is null)
                    {
                        button.Tag = "NativeHover";
                        var track = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = handle };
                        TrackMouseEvent(ref track); // TME_LEAVE | TME_NONCLIENT
                    }
                    break;
                case 0x00A0: case 0x02A2: case 0x0200: // elsewhere in the caption, WM_NCMOUSELEAVE or client mouse move
                    if (button.Tag is not null) button.Tag = null;
                    break;
            }
            return IntPtr.Zero; // DefWindowProc owns NC clicks, maximize/restore and Snap Layouts.
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public int Size, Flags; public IntPtr Window; public uint HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse track);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
