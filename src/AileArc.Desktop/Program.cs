using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AileArc.Core;
using AileArc.DesktopBridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AileArc.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        app.Run(new BrowserWindow(args.FirstOrDefault()));
    }
}

internal sealed class BrowserWindow : Window
{
    private readonly WebView2 web = new();
    private readonly ArchiveWorkerClient client = new(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
    private readonly string? initialPath;
    private ArchivePresentation? archive;
    private CancellationTokenSource? scanCancellation, queryCancellation;
    private string? openedPath, retryPath, password;
    private bool closed, started, picking;
    private int generation, codePage;

    public BrowserWindow(string? path)
    {
        initialPath = path;
        Title = "AileArc · 桌面只读验证"; Width = 1320; Height = 850; MinWidth = 900; MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(30,29,28)); Content = web;
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255,30,29,28);
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { closed = true; scanCancellation?.Cancel(); queryCancellation?.Cancel(); password = null; web.Dispose(); };
    }
    private async Task InitializeAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AileArc", "DesktopValidation", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            if (closed) return;
            await web.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var core = web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => e.Cancel = !BrowserProtocol.TrustedDocument(e.Uri);
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!BrowserProtocol.TrustedResource(e.Request.Uri))
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            };
            core.WebMessageReceived += async (_, e) => await ReceiveAsync(e.Source, e.WebMessageAsJson);
            core.ProcessFailed += (_, _) => { if (!closed) MessageBox.Show(this, "界面进程已退出，请关闭并重新打开桌面验证版。", "AileArc"); };
            string assets = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException();
            core.SetVirtualHostNameToFolderMapping("ailearc.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate(BrowserProtocol.Document);
        }
        catch (Exception)
        {
            if (closed) return;
            Content = new TextBlock { Text = "无法初始化桌面界面。请确认已安装 Microsoft Edge WebView2 Runtime，并使用 Build-Desktop.ps1 构建完整文件。", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40), FontSize = 16 };
        }
    }
    private void Send(object message)
    {
        if (!closed && web.CoreWebView2 is { } core && BrowserProtocol.TrustedDocument(core.Source))
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, BrowserProtocol.Json));
    }
    private async Task ReceiveAsync(string source, string json)
    {
        BrowserRequest request;
        try { request = BrowserProtocol.Parse(source, json); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return; }
        try
        {
            switch (request.Action)
            {
                case "ready":
                    Send(new { kind = "ready", runtime = web.CoreWebView2.Environment.BrowserVersionString });
                    if (!started) { started = true; if (initialPath is not null) await ScanAsync(Path.GetFullPath(initialPath)); }
                    break;
                case "pickArchive":
                    if (picking || scanCancellation is not null) return;
                    picking = true;
                    try
                    {
                        var picker = new Microsoft.Win32.OpenFileDialog { Title = "打开压缩包", Filter = "压缩包|*.zip;*.7z;*.rar|所有文件|*.*", CheckFileExists = true, Multiselect = false, RestoreDirectory = true,
                            InitialDirectory = openedPath is null ? string.Empty : Path.GetDirectoryName(openedPath) ?? string.Empty };
                        if (picker.ShowDialog(this) == true) { password = null; codePage = 0; await ScanAsync(picker.FileName); }
                    }
                    finally { picking = false; }
                    break;
                case "password":
                    if (retryPath is null || scanCancellation is not null) return;
                    password = request.Password;
                    await ScanAsync(retryPath);
                    break;
                case "encoding":
                    if (openedPath is null || scanCancellation is not null || archive?.Scan.Format != "ZIP") return;
                    codePage = request.CodePage;
                    await ScanAsync(openedPath);
                    break;
                case "cancel": scanCancellation?.Cancel(); password = null; retryPath = null; Send(new { kind = "passwordDismissed" }); break;
                case "query":
                    var snapshot = archive;
                    if (snapshot is null) { Send(new { kind = "page", id = request.Id, page = new BrowserPage([],0,0,false) }); break; }
                    queryCancellation?.Cancel();
                    using (var cancellation = new CancellationTokenSource())
                    {
                        queryCancellation = cancellation;
                        try
                        {
                            var page = await Task.Run(() => snapshot.Query(request, cancellation.Token), cancellation.Token);
                            if (!cancellation.IsCancellationRequested && ReferenceEquals(snapshot, archive)) Send(new { kind = "page", id = request.Id, page });
                        }
                        catch (Exception error) when (cancellation.IsCancellationRequested && (error is OperationCanceledException or InvalidOperationException)) { }
                        finally { if (ReferenceEquals(queryCancellation,cancellation)) queryCancellation = null; }
                    }
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Send(new { kind = "error", id = request.Id, code = "OpenFailed" }); }
    }
    private async Task ScanAsync(string path)
    {
        scanCancellation?.Cancel(); queryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); scanCancellation = cancellation;
        int version = ++generation;
        Send(new { kind = "loading", name = Path.GetFileName(path) });
        try
        {
            var progress = new Progress<int>(count => { if (!closed && generation == version && !cancellation.IsCancellationRequested) Send(new { kind = "progress", count }); });
            var scan = await client.ScanAsync(path, progress, cancellation.Token, password, codePage);
            var presentation = await Task.Run(() => new ArchivePresentation(scan,cancellation.Token), cancellation.Token);
            var folders = await Task.Run(() => presentation.GetRootFolders(cancellation.Token), cancellation.Token);
            if (closed || generation != version || cancellation.IsCancellationRequested) return;
            archive = presentation; openedPath = path; retryPath = null;
            Title = Path.GetFileName(path) + " — AileArc · 只读验证";
            Send(new { kind = "archive", name = Path.GetFileName(path), format = scan.Format, bytes = scan.Identity?.Length ?? 0,
                entryCount = presentation.Index.Entries.Count, folders, codePage });
        }
        catch (OperationCanceledException) { if (generation == version) Send(new { kind = "cancelled" }); }
        catch (ArchiveOperationException error)
        {
            if (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
            { retryPath = path; Send(new { kind = "passwordRequired", name = Path.GetFileName(path), code = error.Code }); }
            else { password = null; retryPath = null; Send(new { kind = "error", code = error.Code }); }
        }
        catch (Exception) { password = null; retryPath = null; Send(new { kind = "error", code = "OpenFailed" }); }
        finally { if (ReferenceEquals(scanCancellation,cancellation)) scanCancellation = null; }
    }
}
