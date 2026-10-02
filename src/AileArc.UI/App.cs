using AileArc.Core;
using Microsoft.UI.Xaml;
using AileArc.Core.Lifecycle;
using AileArc.Core.WorkCopies;
using Microsoft.UI.Dispatching;

namespace AileArc.UI;

public sealed partial class App : Application
{
    private readonly ActivationRequest initial;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly List<MainWindow> windows = [];
    private readonly WindowCatalog<MainWindow> catalog = new();
    private readonly Queue<ActivationRequest> pending = new();
    private readonly SemaphoreSlim routing = new(1, 1);
    private LanguageService? language;
    private volatile bool restarting;
    public WorkCopyStore WorkCopies { get; } = new();
    public bool AnyTaskRunning => windows.Any(w => w.IsBusy);
    public void ForgetWorkCopy(string id) { foreach (var window in windows) window.ForgetWorkCopy(id); }
    public static App Instance => (App)Current;
    public App(ActivationRequest request)
    {
        initial = request;
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        language = new LanguageService(settings.Language);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Ailety\AileArc");
            key.SetValue("ShellLanguage", settings.Language);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        await HandleActivationAsync(initial);
        while (pending.TryDequeue(out var request)) await HandleActivationAsync(request);
    }
    public bool EnqueueActivation(ActivationRequest request) => !restarting && dispatcher.TryEnqueue(async () =>
    {
        if (language is null) pending.Enqueue(request);
        else await HandleActivationAsync(request);
    });
    private async Task HandleActivationAsync(ActivationRequest request)
    {
        if (request.Action == ActivationAction.Create)
        {
            var window = CreateWindow();
            window.BringForward();
            _ = window.RunShellActionAsync(request.Action, request.Paths);
            return;
        }
        if (request.Paths.Length == 0)
        {
            var window = !request.NewWindow && windows.Count > 0 ? windows[^1] : CreateWindow();
            window.BringForward();
            return;
        }
        foreach (string path in request.Paths) await OpenArchiveAsync(path, forceNew: request.NewWindow, action: request.Action);
    }
    private MainWindow CreateWindow()
    {
        var window = new MainWindow(language!);
        windows.Add(window);
        window.Closed += (_, _) => { windows.Remove(window); catalog.Remove(window); if (windows.Count == 0) Exit(); };
        return window;
    }
    public async Task OpenArchiveAsync(string path, MainWindow? preferred = null, bool forceNew = false, ActivationAction action = ActivationAction.Open)
    {
        await routing.WaitAsync();
        try
        {
            string? fileId = await ArchiveWindowIdentity.ReadAsync(path);
            var existing = forceNew ? null : catalog.Find(path, fileId);
            if (existing is not null)
            {
                existing.BringForward();
                if (action != ActivationAction.Open) _ = existing.RunShellActionAsync(action, [path]);
                return;
            }
            var window = preferred is not null && preferred.OpenPath is null && !preferred.IsBusy ? preferred : CreateWindow();
            catalog.Register(window, path, fileId);
            window.BringForward();
            if (action == ActivationAction.Open) _ = window.OpenArchiveAsync(path);
            else _ = window.RunShellActionAsync(action, [path], openFirst: true);
        }
        finally { routing.Release(); }
    }
    public async Task RestartAsync()
    {
        if (restarting) return;
        restarting = true;
        try
        {
            var snapshot = windows.ToArray();
            foreach (var window in snapshot) { window.BringForward(); if (!await window.PrepareToCloseAsync()) return; }
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--restart-from"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (string path in snapshot.Select(w => w.OpenPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
            foreach (var window in snapshot) window.CloseApproved();
        }
        finally { restarting = false; }
    }
}
