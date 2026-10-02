using Microsoft.UI.Xaml;
using AileArc.Core.Lifecycle;
using System.Runtime.InteropServices;

namespace AileArc.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length >= 2 && args[0] == "--restart-from" && int.TryParse(args[1], out int oldPid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); if (!old.WaitForExit(15000)) return; }
            catch (ArgumentException) { }
            args = args[2..];
        }
        ActivationRequest request;
        try { request = ActivationRequest.Parse(args, Environment.CurrentDirectory); }
        catch (ArgumentException) { MessageBox(IntPtr.Zero, "无法识别启动参数。 / Invalid launch arguments.", "AileArc", 0x10); return; }
        string key = ActivationBroker.ApplicationKey(Environment.ProcessPath!);
        using var broker = ActivationBroker.TryRegister(key);
        if (broker is null)
        {
            try { ActivationBroker.ForwardAsync(key, request).GetAwaiter().GetResult(); }
            catch (Exception) { MessageBox(IntPtr.Zero, "已有 AileArc 暂时无法响应，请稍后重试。 / The running AileArc is not responding. Please retry.", "AileArc", 0x10); }
            return;
        }
        Application.Start(initialization =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App(request);
            broker.Listen(app.EnqueueActivation);
        });
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint flags);
}
