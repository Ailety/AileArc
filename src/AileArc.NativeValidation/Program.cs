using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AileArc.Core;
using AileArc.DesktopBridge;

// Validation-only, read-only local adapter. The archive is chosen on the command line,
// never by a network request. No write, extraction, shell or arbitrary-path endpoints.
if (args.Length != 2) { Console.Error.WriteLine("Usage: AileArc.NativeValidation <native-executable> <archive>"); return 2; }
string executable = Path.GetFullPath(args[0]), archivePath = Path.GetFullPath(args[1]);
var timer = Stopwatch.StartNew();
var client = new ArchiveWorkerClient(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
ArchivePresentation archive;
try { archive = new(await client.ScanAsync(archivePath)); }
catch (ArchiveOperationException error) { Console.Error.WriteLine($"Scan failed: {error.Code}"); return 3; }
Console.WriteLine($"scanAndIndexMs={timer.ElapsedMilliseconds}; entries={archive.Index.Entries.Count}");
string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
// Bind a random loopback port. A race fails closed rather than binding another address.
var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
using var listener = new HttpListener();
string endpoint = $"http://127.0.0.1:{port}/";
listener.Prefixes.Add(endpoint); listener.Start();
Console.WriteLine($"adapterPort={port}");
var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
start.Environment["AILEARC_ENDPOINT"] = endpoint;
start.Environment["AILEARC_TOKEN"] = secret;
using var ui = Process.Start(start) ?? throw new IOException("Cannot launch native UI.");
using var lifetime = new CancellationTokenSource();
var exit = ui.WaitForExitAsync().ContinueWith(_ => { lifetime.Cancel(); listener.Stop(); }, TaskScheduler.Default);
try
{
    while (!lifetime.IsCancellationRequested)
    {
        var context = await listener.GetContextAsync();
        try
        {
            var request = context.Request;
            if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != "/page" ||
                request.RawUrl?.Length > 8192 || request.Headers["Origin"] is not null ||
                request.Headers["X-AileArc-Token"] != secret)
            { Console.WriteLine("requestRejected=403"); context.Response.StatusCode = 403; continue; }
            string Decode(string key, int max)
            {
                string value = request.QueryString[key] ?? "";
                if (value.Length > max * 8) throw new InvalidDataException();
                string decoded = new UTF8Encoding(false, true).GetString(Convert.FromHexString(value));
                if (decoded.Length > max) throw new InvalidDataException();
                return decoded;
            }
            if (!int.TryParse(request.QueryString["offset"], out int offset) || offset < 0 || offset > 500000)
                throw new InvalidDataException();
            var query = new BrowserRequest(1, Guid.NewGuid().ToString(), "query", Decode("directory", 512), Decode("search", 512), Offset: offset);
            timer.Restart();
            var page = archive.Query(query, lifetime.Token);
            static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
            var body = new StringBuilder($"AILEARC1|{page.Total}|{page.Offset}|{archive.Index.Entries.Count}|{Hex(Path.GetFileName(archivePath))}\n");
            foreach (var row in page.Rows)
                body.Append($"{row.Id}|{(row.Type == "folder" ? 1 : 0)}|{Hex(row.Name)}|{Hex(row.Path)}|{Hex(row.Size?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—")}\n");
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString());
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, lifetime.Token);
            Console.WriteLine($"queryMs={timer.ElapsedMilliseconds}; total={page.Total}; offset={page.Offset}; rows={page.Rows.Length}; bytes={bytes.Length}");
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or DecoderFallbackException or ArgumentException)
        { context.Response.StatusCode = 400; }
        finally { context.Response.Close(); }
    }
}
catch (Exception error) when (lifetime.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
finally { if (!ui.HasExited) ui.Kill(entireProcessTree: true); }
await exit;
return 0;
