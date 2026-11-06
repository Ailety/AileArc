using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AileArc.Client;

internal static class MonitorWorkArea
{
    internal static void Install(Window window) => window.SourceInitialized += (_, _) => Observe(window);

    private static void Observe(Window window)
    {
        double minimumWidth = window.MinWidth, minimumHeight = window.MinHeight;
        double maximumWidth = window.MaxWidth, maximumHeight = window.MaxHeight;
        IntPtr handle = new WindowInteropHelper(window).Handle, previousMonitor = IntPtr.Zero;
        bool pending = false;
        void Fit()
        {
            pending = false;
            var monitor = MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            uint dpi = GetDpiForWindow(handle);
            if (dpi == 0 || !GetMonitorInfo(monitor, ref info)) return;
            previousMonitor = monitor;
            var available = LogicalSize(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, dpi);
            window.MinWidth = Math.Min(minimumWidth, available.Width);
            window.MinHeight = Math.Min(minimumHeight, available.Height);
            if (window.SizeToContent != SizeToContent.Manual)
            {
                window.MaxWidth = Math.Min(maximumWidth, available.Width);
                window.MaxHeight = Math.Min(maximumHeight, available.Height);
            }
            else if (window.WindowState == WindowState.Normal)
            {
                if (window.Width > available.Width) window.Width = available.Width;
                if (window.Height > available.Height) window.Height = available.Height;
            }
        }
        void Schedule()
        {
            if (handle == IntPtr.Zero || pending) return;
            pending = true; window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (handle != IntPtr.Zero) Fit(); }));
        }
        Fit();
        void PlaceInitially(object? sender, EventArgs e)
        {
            window.ContentRendered -= PlaceInitially;
            if (window.WindowStartupLocation == WindowStartupLocation.Manual || window.WindowState != WindowState.Normal) return;
            Fit();
            if (!TryBounds(window, out var bounds, out var work)) return;
            int x = Math.Clamp(bounds.X, work.X, Math.Max(work.X, work.X + work.Width - bounds.Width));
            int y = Math.Clamp(bounds.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - bounds.Height));
            if (bounds.X != x || bounds.Y != y) SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x15); // no size/z-order/focus change
        }
        window.ContentRendered += PlaceInitially;
        window.LocationChanged += (_, _) => { if (handle != IntPtr.Zero && MonitorFromWindow(handle, 2) != previousMonitor) Schedule(); };
        window.DpiChanged += (_, _) => Schedule();
        window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Normal) Schedule(); };
        window.Closed += (_, _) => handle = IntPtr.Zero;
    }

    internal static Size LogicalSize(int width, int height, uint dpi) =>
        new(Math.Floor(width * 96d / dpi), Math.Floor(height * 96d / dpi));

    internal static bool TryBounds(Window window, out Int32Rect bounds, out Int32Rect work)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        bounds = work = default;
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out var rect)) return false;
        bounds = rect.ToRect(); work = info.Work.ToRect(); return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bounds
    {
        public int Left, Top, Right, Bottom;
        public Int32Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Bounds Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
