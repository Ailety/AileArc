using System.Diagnostics;
using AileArc.Shared;

namespace AileArc.Core;

public sealed class ArchiveOperationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ArchiveScan(string Format, IReadOnlyList<ArchiveEntry> Entries, ArchiveIdentity? Identity = null, int NameCodePage = 0);

/// <summary>Owns a single Worker lifetime. Cancellation tears down native parsing as well as IPC.</summary>
public sealed partial class ArchiveWorkerClient(string workerPath)
{
    public async Task<ArchiveScan> ScanAsync(string path, IProgress<int>? progress = null, CancellationToken cancellationToken = default, string? password = null, int nameCodePage = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var start = new ProcessStartInfo(Path.GetFullPath(workerPath))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerPath))!
        };
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new ArchiveOperationException("WorkerFailed");
        }
        catch (System.ComponentModel.Win32Exception) { throw new ArchiveOperationException("WorkerUnavailable"); }
        using var job = WorkerJob.Attach(process);
        // Drain without retaining potentially sensitive native diagnostics in memory or logs.
        Task drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream,
                new ScanRequest(ArchiveProtocol.Version, Path.GetFullPath(path), Password: password, NameCodePage: nameCodePage), token);
            process.StandardInput.Close();
            var entries = new List<ArchiveEntry>();
            var ids = new HashSet<long>();
            long characters = 0;
            string? format = null;
            ArchiveIdentity? identity = null;
            while (true)
            {
                var message = await ArchiveProtocol.ReadAsync<ScanMessage>(process.StandardOutput.BaseStream, token);
                switch (message.Kind)
                {
                    case "opened" when format is null && message.Format is not null:
                        format = message.Format;
                        identity = message.Identity ?? throw new ArchiveOperationException("ProtocolMismatch");
                        break;
                    case "entries" when format is not null && message.Entries is not null:
                        foreach (var entry in message.Entries)
                        {
                            if (entry.Path is null || entry.Path.Length > 32768 || !ids.Add(entry.Id))
                                throw new ArchiveOperationException("ProtocolMismatch");
                            characters += entry.Path.Length;
                            if (entries.Count >= ArchiveProtocol.MaxEntries || characters > ArchiveProtocol.MaxMetadataCharacters)
                                throw new ArchiveOperationException("ResourceLimit");
                            entries.Add(entry);
                        }
                        progress?.Report(entries.Count);
                        break;
                    case "completed" when format is not null:
                        await process.WaitForExitAsync(token);
                        if (process.ExitCode != 0) throw new ArchiveOperationException("WorkerFailed");
                        return new ArchiveScan(format, entries, identity, nameCodePage);
                    case "error": throw new ArchiveOperationException(message.ErrorCode ?? "WorkerFailed");
                    default: throw new ArchiveOperationException("ProtocolMismatch");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ArchiveOperationException("TimedOut"); }
        catch (EndOfStreamException) { throw new ArchiveOperationException("WorkerFailed"); }
        catch (InvalidDataException) { throw new ArchiveOperationException("ProtocolMismatch"); }
        catch (IOException) { throw new ArchiveOperationException("WorkerFailed"); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync();
            await drain;
        }
    }
}
