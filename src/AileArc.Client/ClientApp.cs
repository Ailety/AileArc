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

public sealed partial class ClientApp : Application
{
    internal AileArc.Core.WorkCopies.WorkCopyStore WorkCopies { get; set; } = new();
    internal RecoveryStore Recovery { get; set; } = RecoveryStore.Default;
    internal string PreferencesKey { get; set; } = @"Software\Ailety\AileArc\Preferences";
    internal bool OpenAfterExtraction
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PreferencesKey);
                return key?.GetValue("OpenAfterExtraction") is 1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
        set
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(PreferencesKey);
            key.SetValue("OpenAfterExtraction", value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }
    internal bool HideTestWindows { get; set; }
    private string? restartProbeRoot;
    private string? nextProbeMode;
    private readonly Dictionary<(AileArc.Shared.ArchiveIdentity Identity, long Id, string EntryPath), (MainWindow Owner, Task Request)> pendingFileOpens = [];

    internal Task OpenEntryOnceAsync(MainWindow owner, AileArc.Shared.ArchiveIdentity identity, long id, string entryPath, Func<Task> open)
    {
        Dispatcher.VerifyAccess();
        var key = (identity, id, entryPath);
        if (pendingFileOpens.TryGetValue(key, out var pending))
        {
            if (pending.Owner != owner && pending.Owner.IsVisible) pending.Owner.BringForward();
            return pending.Request;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingFileOpens.Add(key, (owner, completion.Task)); // Before any modal prompt can reenter the dispatcher.
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try { await open(); completion.TrySetResult(); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { pendingFileOpens.Remove(key); }
        }
    }
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
        if (args is ["--self-test", "measure", var mode, var archive, var stamp] &&
            mode is "open" or "cancel" && long.TryParse(stamp, out long launchTick))
            return ClientMeasurement.Run(archive, mode, launchTick);
        if (args is ["--self-test", "recovery-probe", var target, var journal]) return ClientChecks.RecoveryProbe(target, journal);
        if (args is ["--self-test", var layoutLanguage, "--layout-output", var output] && LanguageService.Supported.Contains(layoutLanguage))
        { Text = new(layoutLanguage); return ClientChecks.Run(new ClientApp(), Path.GetFullPath(output)); }
        if (args is ["--self-test", var language]) { Text = new(language); return ClientChecks.Run(new ClientApp()); }
        string? probe = null;
        string? probeMode = null;
        if (args is ["--self-test", "language-restart", var probeFolder])
        {
            if (Console.ReadLine() != "start") return 2;
            probe = Path.GetFullPath(probeFolder); probeMode = "language-restart";
            args = ClientLanguageProbe.CreateSession(probe);
        }
        else if (args is ["--self-test", "language-restored" or "language-returned", var restoredFolder, "--restart-from", _, "--restore-session", _])
        { probe = Path.GetFullPath(restoredFolder); probeMode = args[1]; args = args[3..]; }
        if (args is ["--restart-from", var pidText, "--restore-session", var session])
        {
            if (!int.TryParse(pidText, out int pid) || pid <= 0 || pid == Environment.ProcessId) return 2;
            try { using var previous = Process.GetProcessById(pid); if (!previous.WaitForExit(15000)) return 3; }
            catch (ArgumentException) { }
            args = ["--restore-session", session];
        }
        var settings = probe is null ? AppSettings.LoadAsync() : AppSettings.LoadAsync(Path.Combine(probe, "settings.json"));
        Text = new(settings.GetAwaiter().GetResult().Language);
        StartupArchive[] archives = [];
        ActivationRequest? launch = null;
        try
        {
            if (args is ["--restore-session", _]) archives = ParseArguments(args);
            else launch = ActivationRequest.Parse(args, Environment.CurrentDirectory);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { MessageBox.Show(Text["ClientInvalidArguments"], "AileArc"); return 2; }
        string key = ActivationBroker.ApplicationKey(probe is null ? Environment.ProcessPath! : Path.Combine(probe, "probe.exe"));
        using var broker = ActivationBroker.TryRegister(key);
        if (broker is null)
        {
            try
            {
                if (launch is not null) ActivationBroker.ForwardAsync(key, launch).GetAwaiter().GetResult();
                else if (archives.Length == 0) ActivationBroker.ForwardAsync(key, new([])).GetAwaiter().GetResult();
                else foreach (var item in archives)
                {
                    var request = item.Empty ? new ActivationRequest([], true) : new ActivationRequest([Path.GetFullPath(item.Path)], true, CodePages: [item.CodePage]);
                    ActivationBroker.ForwardAsync(key, request).GetAwaiter().GetResult();
                }
                return 0;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ArgumentException)
            { MessageBox.Show(Text["ClientActivationFailed"], "AileArc"); return 3; }
        }
        var app = new ClientApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (probe is not null)
        {
            app.HideTestWindows = true; app.restartProbeRoot = probe;
            app.nextProbeMode = probeMode == "language-restart" ? "language-restored" : "language-returned";
            app.WriteSettings = choice => choice.SaveAsync(Path.Combine(probe, "settings.json"));
        }
        broker.Listen(app.EnqueueActivation);
        app.Startup += async (_, _) =>
        {
            try
            {
                try
                {
                    if (probe is null)
                    {
                        using var languageKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Ailety\AileArc");
                        languageKey.SetValue("ShellLanguage", Text.Language);
                    }
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                if (launch is not null) await app.HandleActivationAsync(launch);
                else if (archives.Length == 0) app.CreateWindow().Show();
                else foreach (var archive in archives)
                    if (archive.Empty) app.CreateWindow().Show(); else await app.OpenPathAsync(archive.Path, archive.CodePage, forceNew: true);
                if (probe is not null) await ClientLanguageProbe.CheckAsync(app, probe, probeMode!);
            }
            catch (Exception error) when (probe is not null)
            {
                File.WriteAllText(Path.Combine(probe, "failure.txt"), error.ToString()); app.Shutdown(1);
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
            if (initialActivation || Restarting || SavingLanguage || pendingActivations != 0 || routing.CurrentCount == 0 || Windows.OfType<MainWindow>().Any()) return;
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
            var identity = await ArchiveWindowIdentity.ReadSnapshotAsync(path);
            if (Restarting) return;
            var existing = forceNew ? null : catalog.Find(path, identity?.FileId);
            if (existing is not null)
            {
                existing.BringForward();
                if (action != ActivationAction.Open)
                {
                    bool reload = identity is null || identity != existing.CurrentIdentity || !string.Equals(path, existing.OpenedPath, StringComparison.OrdinalIgnoreCase);
                    existing.QueueShellAction(action, [path], reload);
                }
                else if (identity is not null && identity != existing.CurrentIdentity && !existing.ShellActionPending)
                    await existing.ReloadActivatedPathAsync(path);
                return;
            }
            var window = preferred is not null && preferred.OpenedPath is null && !preferred.IsBusy ? preferred : CreateWindow();
            // The worker can enumerate while WPF creates and lays out the HWND.
            // Bring the window forward synchronously before yielding to UI callbacks.
            var opening = window.OpenArchiveAsync(path, codePage);
            window.BringForward();
            await opening;
            if (window.OpenedPath is not null && action != ActivationAction.Open) window.QueueShellAction(action, [path]);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { MessageBox.Show(Text["OpenFailed"], "AileArc"); }
        finally { routing.Release(); ExitIfIdle(); }
    }
    internal enum RestartResult { Started, Busy, Declined, Failed }
    public async Task RestartAsync() => await TryRestartAsync();
    internal async Task<RestartResult> TryRestartAsync()
    {
        lock (activationGate)
        {
            if (Restarting || SavingLanguage || AnyTaskRunning || pendingActivations != 0 || routing.CurrentCount == 0 || cleanupOwner is not null) return RestartResult.Busy;
            Restarting = true;
        }
        var windows = Windows.OfType<MainWindow>().ToArray();
        var enabled = windows.Select(window => window.IsEnabled).ToArray();
        bool handedOff = false;
        foreach (var window in windows) window.IsEnabled = false;
        try
        {
            foreach (var window in windows) if (!await window.PrepareCloseAsync()) return RestartResult.Declined;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            var archives = windows.Select(w => w.OpenedPath is { } path ? new StartupArchive(path, w.CurrentCodePage) : new StartupArchive("", Empty: true)).ToArray();
            string json = JsonSerializer.Serialize(archives);
            // Paths and encoding only: passwords never cross this process boundary.
            if (json.Length > 24000 || archives.Length > 64) return RestartResult.Failed;
            if (restartProbeRoot is not null)
            {
                start.ArgumentList.Add("--self-test"); start.ArgumentList.Add(nextProbeMode!); start.ArgumentList.Add(restartProbeRoot);
                start.WindowStyle = ProcessWindowStyle.Hidden;
            }
            start.ArgumentList.Add("--restart-from"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("--restore-session"); start.ArgumentList.Add(json);
            try
            {
                using var replacement = Process.Start(start);
                if (replacement is null) return RestartResult.Failed;
                handedOff = true;
                foreach (var window in windows) window.ApproveClose();
                Shutdown(); return RestartResult.Started;
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException) { return RestartResult.Failed; }
        }
        finally
        {
            if (!handedOff)
            {
                Restarting = false;
                for (int i = 0; i < windows.Length; i++) windows[i].IsEnabled = enabled[i];
            }
        }
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => ClientApp.Text[key];
}
