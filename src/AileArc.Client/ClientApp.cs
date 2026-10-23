using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Text.Json;
using System.Text.Json.Serialization;
using AileArc.Core;
using AileArc.Core.Lifecycle;
using AileArc.Core.Recovery;

namespace AileArc.Client;

public sealed class ClientApp : Application
{
    internal AileArc.Core.WorkCopies.WorkCopyStore WorkCopies { get; set; } = new();
    internal RecoveryStore Recovery { get; set; } = RecoveryStore.Default;
    internal bool HideTestWindows { get; set; }
    private volatile bool restarting;
    internal bool Restarting { get => restarting; private set => restarting = value; }
    private readonly WindowCatalog<MainWindow> catalog = new();
    private readonly SemaphoreSlim routing = new(1, 1);
    private int pendingActivations;
    private bool initialActivation = true;
    private MainWindow? cleanupOwner;
    private readonly object activationGate = new();
    private bool exiting;
    internal bool AnyTaskRunning => Windows.OfType<MainWindow>().Any(w => w.IsBusy || w.ShellActionPending);
    internal bool CanStart(MainWindow window) => !Restarting && (cleanupOwner is null || cleanupOwner == window);
    internal bool TryBeginCleanup(MainWindow window)
    {
        lock (activationGate)
        {
            if (Restarting || cleanupOwner is not null || AnyTaskRunning || routing.CurrentCount == 0 || pendingActivations != 0) return false;
            cleanupOwner = window;
        }
        RefreshControls(); return true;
    }
    internal void EndCleanup() { lock (activationGate) cleanupOwner = null; RefreshControls(); }
    private void RefreshControls() { foreach (var window in Windows.OfType<MainWindow>()) window.RefreshControls(); }
    internal void ForgetCopy(string id) { foreach (var window in Windows.OfType<MainWindow>()) window.ForgetCopy(id); }
    public static LanguageService Text { get; internal set; } = new(LanguageService.DefaultLanguage);
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--self-test", "recovery-probe", var target, var journal]) return ClientChecks.RecoveryProbe(target, journal);
        if (args is ["--self-test", var language]) { Text = new(language); return ClientChecks.Run(new ClientApp()); }
        if (args is ["--restart-from", var pidText, "--restore-session", var session])
        {
            if (!int.TryParse(pidText, out int pid) || pid <= 0 || pid == Environment.ProcessId) return 2;
            try { using var previous = Process.GetProcessById(pid); if (!previous.WaitForExit(15000)) return 3; }
            catch (ArgumentException) { }
            args = ["--restore-session", session];
        }
        Text = new(AppSettings.LoadAsync().GetAwaiter().GetResult().Language);
        StartupArchive[] archives = [];
        ActivationRequest? launch = null;
        try
        {
            if (args is ["--restore-session", _]) archives = ParseArguments(args);
            else launch = ActivationRequest.Parse(args, Environment.CurrentDirectory);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { MessageBox.Show(Text["ClientInvalidArguments"], "AileArc"); return 2; }
        string key = ActivationBroker.ApplicationKey(Environment.ProcessPath!);
        using var broker = ActivationBroker.TryRegister(key);
        if (broker is null)
        {
            try
            {
                if (launch is not null) ActivationBroker.ForwardAsync(key, launch).GetAwaiter().GetResult();
                else if (archives.Length == 0) ActivationBroker.ForwardAsync(key, new([])).GetAwaiter().GetResult();
                else foreach (var item in archives)
                {
                    var request = item.Empty ? new ActivationRequest([], true) : new ActivationRequest([Path.GetFullPath(item.Path)], CodePages: [item.CodePage]);
                    ActivationBroker.ForwardAsync(key, request).GetAwaiter().GetResult();
                }
                return 0;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ArgumentException)
            { MessageBox.Show(Text["ClientActivationFailed"], "AileArc"); return 3; }
        }
        var app = new ClientApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        broker.Listen(app.EnqueueActivation);
        app.Startup += async (_, _) =>
        {
            try
            {
                try
                {
                    using var languageKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Ailety\AileArc");
                    languageKey.SetValue("ShellLanguage", Text.Language);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                if (launch is not null) await app.HandleActivationAsync(launch);
                else if (archives.Length == 0) app.CreateWindow().Show();
                else foreach (var archive in archives)
                    if (archive.Empty) app.CreateWindow().Show(); else await app.OpenPathAsync(archive.Path, archive.CodePage);
            }
            finally { app.initialActivation = false; app.ExitIfIdle(); }
        };
        return app.Run();
    }
    internal sealed record StartupArchive(string Path, int CodePage = 0, bool Empty = false);
    internal static StartupArchive[] ParseArguments(string[] args)
    {
        if (args is ["--new-window"]) return [];
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
        if (archives.Length > 64 || archives.Any(item => item is null ||
            (item.Empty ? item.Path != "" || item.CodePage != 0 : string.IsNullOrWhiteSpace(item.Path)) ||
            item.CodePage is not (0 or 65001 or 936 or 932))) throw new ArgumentException("Invalid session.");
        return archives;
    }
    internal bool EnqueueActivation(ActivationRequest request)
    {
        lock (activationGate)
        {
            if (Dispatcher.HasShutdownStarted || Restarting || exiting || cleanupOwner is not null || pendingActivations >= 64) return false;
            Interlocked.Increment(ref pendingActivations);
            try
            {
                var operation = Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await HandleActivationAsync(request);
                    }
                    finally { Interlocked.Decrement(ref pendingActivations); ExitIfIdle(); }
                }));
                if (operation.Status != System.Windows.Threading.DispatcherOperationStatus.Aborted) return true;
            }
            catch (InvalidOperationException) { }
            Interlocked.Decrement(ref pendingActivations); return false;
        }
    }
    internal async Task HandleActivationAsync(ActivationRequest request)
    {
        if (request.Action == ActivationAction.Create)
        {
            var window = CreateWindow(); window.BringForward(); window.QueueShellAction(request.Action, request.Paths); return;
        }
        if (request.Paths.Length == 0)
        {
            var window = !request.NewWindow ? Windows.OfType<MainWindow>().LastOrDefault() : null;
            (window ?? CreateWindow()).BringForward(); return;
        }
        for (int i = 0; i < request.Paths.Length; i++)
            await OpenPathAsync(request.Paths[i], request.CodePages?[i] ?? 0, action: request.Action, forceNew: request.NewWindow);
    }
    private void ExitIfIdle()
    {
        lock (activationGate)
        {
            if (initialActivation || Restarting || pendingActivations != 0 || routing.CurrentCount == 0 || Windows.OfType<MainWindow>().Any()) return;
            exiting = true;
        }
        Shutdown();
    }
    internal MainWindow CreateWindow()
    {
        var window = new MainWindow();
        if (HideTestWindows) { window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false; }
        window.Closed += (_, _) => { catalog.Remove(window); ExitIfIdle(); };
        return window;
    }
    internal void RegisterOpened(MainWindow window, string path, string? fileId)
    { catalog.Remove(window); catalog.Register(window, path, fileId); }
    internal void Unregister(MainWindow window) => catalog.Remove(window);
    public async Task OpenPathAsync(string path, int codePage = 0, MainWindow? preferred = null, ActivationAction action = ActivationAction.Open, bool forceNew = false)
    {
        if (Restarting || cleanupOwner is not null) return;
        await routing.WaitAsync();
        try
        {
            path = Path.GetFullPath(path);
            string? fileId = await ArchiveWindowIdentity.ReadAsync(path);
            if (Restarting) return;
            var existing = forceNew ? null : catalog.Find(path, fileId);
            if (existing is not null)
            {
                existing.BringForward();
                if (action != ActivationAction.Open) existing.QueueShellAction(action, [path]);
                return;
            }
            var window = preferred is not null && preferred.OpenedPath is null && !preferred.IsBusy ? preferred : CreateWindow();
            window.BringForward();
            await window.OpenArchiveAsync(path, codePage);
            if (window.OpenedPath is not null && action != ActivationAction.Open) window.QueueShellAction(action, [path]);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
        finally { routing.Release(); ExitIfIdle(); }
    }
    public async Task RestartAsync()
    {
        lock (activationGate)
        {
            if (Restarting || AnyTaskRunning || pendingActivations != 0 || routing.CurrentCount == 0 || cleanupOwner is not null) return;
            Restarting = true;
        }
        var windows = Windows.OfType<MainWindow>().ToArray();
        foreach (var window in windows) window.IsEnabled = false;
        try
        {
            foreach (var window in windows) if (!await window.PrepareCloseAsync()) return;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            var archives = windows.Select(w => w.OpenedPath is { } path ? new StartupArchive(path, w.CurrentCodePage) : new StartupArchive("", Empty: true)).ToArray();
            string json = JsonSerializer.Serialize(archives);
            // Paths and encoding only: passwords never cross this process boundary.
            if (json.Length > 24000 || archives.Length > 64) { MessageBox.Show(Text["OpenFailed"], "AileArc"); return; }
            start.ArgumentList.Add("--restart-from"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("--restore-session"); start.ArgumentList.Add(json);
            try { Process.Start(start); foreach (var window in windows) window.ApproveClose(); Shutdown(); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException) { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
        }
        finally { Restarting = false; foreach (var window in windows) window.IsEnabled = true; }
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => ClientApp.Text[key];
}
