using AileArc.Core.Extraction;
using AileArc.Shared;

namespace AileArc.Core;

public sealed record ArchiveTestProgress(int Checked, int Total, ulong Bytes);
public sealed record ArchiveTestResult(int Checked, ulong Bytes, IReadOnlyList<ExtractionFailure> Failures);

public sealed partial class ArchiveWorkerClient
{
    public async Task<ArchiveTestResult> TestAsync(string path, ArchiveScan snapshot,
        IProgress<ArchiveTestProgress>? progress = null, CancellationToken cancellationToken = default, string? password = null)
    {
        const ulong limit = 4UL * 1024 * 1024 * 1024;
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Identity is null) throw new ArchiveOperationException("ArchiveChanged");
        var pending = new Dictionary<long, ArchiveEntry>(snapshot.Entries.Count);
        ulong expected = 0;
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong size = entry.Size ?? 0;
            if (size > limit - expected) throw new ArchiveOperationException("TestByteLimit");
            expected += size;
            pending.Add(entry.Id, entry);
        }
        int total = pending.Count;
        using var process = StartWorker();
        using var job = WorkerJob.Attach(process);
        await using var pipe = new WorkerPipe(process, job, idleTimeout, cancellationToken);
        bool opened = false, started = false;
        ulong bytes = 0, reportedBytes = 0;
        var failures = new List<ExtractionFailure>();
        try
        {
            await pipe.WriteAsync(new ScanRequest(ArchiveProtocol.Version, Path.GetFullPath(path), "test", password, limit, snapshot.NameCodePage));
            while (true)
            {
                var message = await pipe.ReadAsync();
                switch (message.Kind)
                {
                    case "activity": break;
                    case "opened" when !opened:
                        if (message.Identity != snapshot.Identity || message.Format != snapshot.Format) throw new ArchiveOperationException("ArchiveChanged");
                        opened = true;
                        break;
                    case "ready" when opened && !started:
                        if (password is null && snapshot.Entries.Any(e => e.Encrypted)) throw new ArchiveOperationException("PasswordRequired");
                        await pipe.WriteAsync(new ExtractionCommand(null));
                        process.StandardInput.Close();
                        started = true;
                        break;
                    case "testing" when started && message.CompletedBytes is ulong amount && amount >= bytes && amount >= reportedBytes && amount <= limit:
                        reportedBytes = amount;
                        progress?.Report(new(total - pending.Count, total, amount));
                        break;
                    case "checked" when started && message.Checked is { Length: > 0 and <= 128 } results:
                        foreach (var item in results)
                        {
                            if (item is null || !pending.Remove(item.Id, out var entry)) throw new ArchiveOperationException("ProtocolMismatch");
                            if (item.Bytes > limit - bytes) throw new ArchiveOperationException("TestByteLimit");
                            bytes += item.Bytes;
                            if (item.Result != 0 || (!entry.IsDirectory && entry.Size is ulong size && size != item.Bytes))
                                failures.Add(ExtractionFailure.ForEntry(entry, item.Result));
                        }
                        progress?.Report(new(total - pending.Count, total, Math.Max(reportedBytes, bytes)));
                        break;
                    case "completed" when started && pending.Count == 0 && bytes >= reportedBytes:
                        await pipe.WaitForExitAsync();
                        if (process.ExitCode != 0) throw new ArchiveOperationException("WorkerFailed");
                        return new(total, bytes, failures);
                    case "error": throw new ArchiveOperationException(message.ErrorCode ?? "ReadFailed");
                    default: throw new ArchiveOperationException("ProtocolMismatch");
                }
            }
        }
        catch (InvalidDataException) { throw new ArchiveOperationException("ProtocolMismatch"); }
        catch (EndOfStreamException) { throw new ArchiveOperationException("WorkerFailed"); }
        catch (IOException) { throw new ArchiveOperationException("WorkerFailed"); }
    }
}
