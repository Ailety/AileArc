using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using AileArc.Core;

namespace AileArc.Client;

public sealed class ClientApp : Application
{
    public static LanguageService Text { get; internal set; } = new(LanguageService.DefaultLanguage);
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--self-test", var language]) { Text = new(language); return ClientChecks.Run(new ClientApp()); }
        Text = new(AppSettings.LoadAsync().GetAwaiter().GetResult().Language);
        if (args.Any(path => path.StartsWith('-')))
        { MessageBox.Show(Text["ClientInvalidArguments"], "AileArc"); return 2; }
        var app = new ClientApp();
        app.Startup += (_, _) =>
        {
            if (args.Length == 0) new MainWindow().Show();
            else foreach (string path in args) app.OpenPath(path);
        };
        return app.Run();
    }
    public void OpenPath(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var existing = Windows.OfType<MainWindow>().FirstOrDefault(w => string.Equals(w.TargetPath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
            new MainWindow(path).Show();
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
    }
    public void Restart()
    {
        if (Windows.OfType<MainWindow>().Any(w => w.IsBusy)) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var window in Windows.OfType<MainWindow>()) if (window.OpenedPath is { } path) start.ArgumentList.Add(path);
        try { Process.Start(start); Shutdown(); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => ClientApp.Text[key];
}
