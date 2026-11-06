using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckFrameAsync(MainWindow window)
    {
        var frame = (DockPanel)window.Content;
        var caption = (DockPanel)frame.Children[0];
        var buttons = caption.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        var open = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "OpenArchive");
        var initialVisibility = open.Visibility;
        var hwnd = new WindowInteropHelper(window).Handle;
        IntPtr Hit(FrameworkElement element, double x, double y)
        {
            var point = element.PointToScreen(new Point(x, y));
            int packed = ((int)point.Y << 16) | ((int)point.X & 0xffff);
            return SendMessage(hwnd, 0x0084, 0, packed);
        }
        var controls = buttons.Where(b => b != open).ToArray();
        async Task PressAndWait(Button button, WindowState target)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (window.WindowState != target && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(15);
            Require(window.WindowState == target, $"caption command changes window state to {target}");
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        try
        {
            open.Visibility = Visibility.Visible;
            window.UpdateLayout();
            Require(controls.Length == 3 && controls.All(b => Math.Abs(b.TranslatePoint(new Point(), caption).Y) < .01 &&
                Math.Abs(b.ActualHeight - caption.ActualHeight) < .01 && Math.Abs(b.ActualWidth - 46) <= 1), "equal full-height caption controls");
            double gap = controls[0].TranslatePoint(new Point(), caption).X - open.TranslatePoint(new Point(open.ActualWidth, 0), caption).X;
            Require(gap >= 15 && Hit(controls[0], -gap / 2, 20) == 2, "draggable gap separates archive actions and window commands");
            Require(Hit(controls[1], 23, 20) == 9 && Hit(open, open.ActualWidth / 2, 15) == 1 &&
                Hit(caption, caption.ActualWidth / 3, 1) == 12 && Hit(controls[2], controls[2].ActualWidth - 2, 2) == 14,
                "native snap, open command, top and top-right resize hit regions");
            if (DwmGetWindowAttribute(hwnd, 33, out int preference, sizeof(int)) == 0)
                Require(preference == 2, "compositor received rounded window preference");
            await PressAndWait(controls[1], WindowState.Maximized);
            Require(Equals(controls[1].ToolTip, ClientApp.Text["ClientRestore"]) && Hit(controls[1], 23, 20) == 9,
                "maximized window exposes restore and Snap");
            var origin = caption.PointToScreen(new Point(0, 0));
            Require(MonitorWorkArea.TryBounds(window, out _, out var work) && Math.Abs(origin.X - work.X) <= 2 &&
                Math.Abs(origin.Y - work.Y) <= 2, $"maximized frame starts at monitor work area: {origin} / {work}");
            await PressAndWait(controls[1], WindowState.Normal);
            await PressAndWait(controls[0], WindowState.Minimized);
        }
        finally
        {
            open.Visibility = initialVisibility;
            window.WindowState = WindowState.Normal;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
