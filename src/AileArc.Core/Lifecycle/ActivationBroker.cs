using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AileArc.Shared;

namespace AileArc.Core.Lifecycle;

public sealed record ActivationRequest(string[] Paths, bool NewWindow = false)
{
    public static ActivationRequest Parse(string[] args, string workingDirectory)
    {
        bool newWindow = false;
        var paths = new List<string>();
        foreach (string arg in args)
        {
            if (arg == "--new-window") { newWindow = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown launch option.");
            if (arg.Length > 32768 || paths.Count >= 32) throw new ArgumentException("Too many launch arguments.");
            paths.Add(Path.GetFullPath(arg, workingDirectory));
        }
        return new(paths.ToArray(), newWindow);
    }
}
public sealed record ActivationReply(bool Accepted, int ProcessId);

/// <summary>A single named-pipe server slot is the atomic instance registration; it stays alive between clients.</summary>
public sealed class ActivationBroker : IDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource stopping = new();
    private Task? listener;
    private ActivationBroker(string name) => server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    public static string ApplicationKey(string executable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string scope = $"{Path.GetFullPath(executable).ToUpperInvariant()}|{identity.User}|{process.SessionId}";
        return "AileArc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];
    }
    public static ActivationBroker? TryRegister(string name)
    {
        try { return new(name); }
        catch (IOException) { return null; }
    }
    public void Listen(Func<ActivationRequest, bool> accept)
    {
        if (listener is not null) throw new InvalidOperationException("Already listening.");
        listener = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(stopping.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await ArchiveProtocol.WriteAsync(server, new ActivationReply(true, Environment.ProcessId), timeout.Token);
                    var request = await ArchiveProtocol.ReadAsync<ActivationRequest>(server, timeout.Token);
                    bool valid = request.Paths is not null && request.Paths.Length <= 32 &&
                        request.Paths.All(ValidPath);
                    bool accepted = valid && accept(request);
                    await ArchiveProtocol.WriteAsync(server, new ActivationReply(accepted, Environment.ProcessId), timeout.Token);
                    _ = await ArchiveProtocol.ReadAsync<bool>(server, timeout.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException) { }
                finally
                {
                    if (!stopping.IsCancellationRequested && server.IsConnected)
                        try { server.Disconnect(); } catch (IOException) { }
                }
            }
        });
    }
    public static async Task ForwardAsync(string name, ActivationRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            var hello = await ArchiveProtocol.ReadAsync<ActivationReply>(client, timeout.Token);
            _ = AllowSetForegroundWindow((uint)hello.ProcessId);
            await ArchiveProtocol.WriteAsync(client, request, timeout.Token);
            var reply = await ArchiveProtocol.ReadAsync<ActivationReply>(client, timeout.Token);
            await ArchiveProtocol.WriteAsync(client, true, timeout.Token);
            if (reply.Accepted) return;
            await Task.Delay(200, timeout.Token);
        }
    }
    public void Dispose()
    {
        stopping.Cancel();
        server.Dispose();
        listener?.GetAwaiter().GetResult();
        stopping.Dispose();
    }
    private static bool ValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || !Path.IsPathFullyQualified(path)) return false;
        try { _ = Path.GetFullPath(path); return true; }
        catch (ArgumentException) { return false; }
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
