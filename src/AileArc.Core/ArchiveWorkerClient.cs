using System.Diagnostics;
using AileArc.Shared;

namespace AileArc.Core;

public sealed class ArchiveOperationException(string code, string? affectedPath = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? AffectedPath { get; } = affectedPath;
}

public sealed record ArchiveScan(string Format, IReadOnlyList<ArchiveEntry> Entries, ArchiveIdentity? Identity = null, int NameCodePage = 0);

/// <summary>Owns a single Worker lifetime. Cancellation tears down native parsing as well as IPC.</summary>
public sealed partial class ArchiveWorkerClient(string workerPath)
{
    private readonly TimeSpan idleTimeout = TimeSpan.FromMinutes(2);
    private sealed class MetadataBudget
    {
        private int consecutive;
        private long characters;
        private HashSet<long>? otherIds;
        public void Accept(ArchiveEntry? entry)
        {
            if (entry?.Path is null || entry.Path.Length > 32768 || entry.Id < consecutive || entry.Id > uint.MaxValue ||
                entry.ModifiedFileTime is <= 0 || entry.ModifiedFileTime > ArchiveEntry.MaxFileTime)
                throw new ArchiveOperationException("ProtocolMismatch");
            // Native enumeration normally sends 0..N-1. After the first gap,
            // keep only the rest in a set, including any out-of-order arrivals.
            if (otherIds is null && entry.Id == consecutive) consecutive++;
            else if (!(otherIds ??= []).Add(entry.Id)) throw new ArchiveOperationException("ProtocolMismatch");
            characters += entry.Path.Length;
            if (consecutive + (otherIds?.Count ?? 0) > ArchiveProtocol.MaxEntries || characters > ArchiveProtocol.MaxMetadataCharacters)
                throw new ArchiveOperationException("ResourceLimit");
        }
    }
    public ArchiveWorkerClient(string workerPath, TimeSpan idleTimeout) : this(workerPath)
    {
        if (idleTimeout < TimeSpan.FromMilliseconds(1) || idleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        this.idleTimeout = idleTimeout;
    }

    private Process StartWorker()
    {
        var start = new ProcessStartInfo(Path.GetFullPath(workerPath))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerPath))!
        };
        try { return Process.Start(start) ?? throw new ArchiveOperationException("WorkerFailed"); }
        catch (System.ComponentModel.Win32Exception) { throw new ArchiveOperationException("WorkerUnavailable"); }
    }

    public async Task<ArchiveScan> ScanAsync(string path, IProgress<int>? progress = null, CancellationToken cancellationToken = default, string? password = null, int nameCodePage = 0, ArchiveIdentity? expectedIdentity = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = StartWorker();
        using var job = WorkerJob.Attach(process);
        await using var pipe = new WorkerPipe(process, job, idleTimeout, cancellationToken);
        try
        {
            await pipe.WriteAsync(new ScanRequest(ArchiveProtocol.Version, Path.GetFullPath(path), Password: password, NameCodePage: nameCodePage, ExpectedIdentity: expectedIdentity));
            process.StandardInput.Close();
            var entries = new List<ArchiveEntry>();
            var budget = new MetadataBudget();
            string? format = null;
            ArchiveIdentity? identity = null;
            while (true)
            {
                var message = await pipe.ReadAsync();
                switch (message.Kind)
                {
                    case "activity" when format is null: break;
                    case "opened" when format is null && message.Format is not null:
                        format = message.Format;
                        identity = message.Identity ?? throw new ArchiveOperationException("ProtocolMismatch");
                        if (expectedIdentity is not null && identity != expectedIdentity) throw new ArchiveOperationException("ArchiveChanged");
                        break;
                    case "entries" when format is not null && message.Entries is { Length: > 0 }:
                        foreach (var entry in message.Entries)
                        {
                            budget.Accept(entry);
                            entries.Add(entry);
                        }
                        progress?.Report(entries.Count);
                        break;
                    case "completed" when format is not null:
                        await pipe.WaitForExitAsync();
                        if (process.ExitCode != 0) throw new ArchiveOperationException("WorkerFailed");
                        return new ArchiveScan(format, entries, identity, nameCodePage);
                    case "error": throw new ArchiveOperationException(message.ErrorCode ?? "WorkerFailed");
                    default: throw new ArchiveOperationException("ProtocolMismatch");
                }
            }
        }
        catch (EndOfStreamException) { throw new ArchiveOperationException("WorkerFailed"); }
        catch (InvalidDataException) { throw new ArchiveOperationException("ProtocolMismatch"); }
        catch (IOException) { throw new ArchiveOperationException("WorkerFailed"); }
    }
}
