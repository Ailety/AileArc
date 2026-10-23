using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        StartupArchive[] archives;
        try { archives = ParseArguments(args); }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { MessageBox.Show(Text["ClientInvalidArguments"], "AileArc"); return 2; }
        var app = new ClientApp();
        app.Startup += (_, _) =>
        {
            if (archives.Length == 0) new MainWindow().Show();
            else foreach (var archive in archives) app.OpenPath(archive.Path, archive.CodePage);
        };
        return app.Run();
    }
    internal sealed record StartupArchive(string Path, int CodePage = 0);
    internal static StartupArchive[] ParseArguments(string[] args)
    {
        StartupArchive[] archives;
        if (args is ["--restore-session", var json])
        {
            if (json.Length > 24000) throw new ArgumentException("Session too large.");
            archives = JsonSerializer.Deserialize<StartupArchive[]>(json, new JsonSerializerOptions { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new ArgumentException("Missing session.");
        }
        else
        {
            if (args.Any(path => path.StartsWith('-'))) throw new ArgumentException("Unsupported command.");
            archives = args.Select(path => new StartupArchive(path)).ToArray();
        }
        if (archives.Length > 64 || archives.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path) || item.CodePage is not (0 or 65001 or 936 or 932))) throw new ArgumentException("Invalid session.");
        return archives;
    }
    public void OpenPath(string path, int codePage = 0)
    {
        try
        {
            path = Path.GetFullPath(path);
            var existing = Windows.OfType<MainWindow>().FirstOrDefault(w => string.Equals(w.TargetPath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
            new MainWindow(path, codePage).Show();
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
    }
    public void Restart()
    {
        if (Windows.OfType<MainWindow>().Any(w => w.IsBusy)) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        var archives = Windows.OfType<MainWindow>().Where(w => w.OpenedPath is not null).Select(w => new StartupArchive(w.OpenedPath!, w.CurrentCodePage)).ToArray();
        string json = JsonSerializer.Serialize(archives);
        // Paths and encoding only: passwords never cross this process boundary.
        if (json.Length > 24000 || archives.Length > 64) { MessageBox.Show(Text["OpenFailed"], "AileArc"); return; }
        start.ArgumentList.Add("--restore-session"); start.ArgumentList.Add(json);
        try { Process.Start(start); Shutdown(); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException) { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => ClientApp.Text[key];
}
