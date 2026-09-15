using System.ComponentModel;
using System.Diagnostics;
using AileArc.Core.Extraction;
using AileArc.Shared;

namespace AileArc.Core;

public sealed partial class ArchiveWorkerClient
{
    public async Task<ExtractionResult> ExtractAsync(string path, ExtractionOptions options,
        Func<string, CancellationToken, Task<ConflictDecision>>? conflict = null,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default, string? password = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.GetFullPath(workerPath))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerPath))!
        };
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new ArchiveOperationException("WorkerFailed"); }
        catch (Win32Exception) { throw new ArchiveOperationException("WorkerUnavailable"); }
        using var job = WorkerJob.Attach(process);
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        ExtractionWriter? writer = null;
        try
        {
            await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream,
                new ScanRequest(ArchiveProtocol.Version, Path.GetFullPath(path), "extract", password, options.ByteLimit, options.NameCodePage), cancellationToken);
            var metadata = new List<ArchiveEntry>();
            var ids = new HashSet<long>();
            long characters = 0;
            bool opened = false;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                ScanMessage message;
                try { message = await ArchiveProtocol.ReadAsync<ScanMessage>(process.StandardOutput.BaseStream, idle.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ArchiveOperationException("TimedOut"); }
                switch (message.Kind)
                {
                    case "opened" when !opened:
                        if (message.Identity is null) throw new ArchiveOperationException("ProtocolMismatch");
                        if (options.ExpectedIdentity is not null && options.ExpectedIdentity != message.Identity)
                            throw new ArchiveOperationException("ArchiveChanged");
                        opened = true;
                        break;
                    case "entries" when opened && writer is null && message.Entries is not null:
                        foreach (var entry in message.Entries)
                        {
                            if (entry.Path is null || entry.Id < 0 || entry.Id > uint.MaxValue || !ids.Add(entry.Id))
                                throw new ArchiveOperationException("ProtocolMismatch");
                            characters += entry.Path.Length;
                            if (metadata.Count >= ArchiveProtocol.MaxEntries || characters > ArchiveProtocol.MaxMetadataCharacters)
                                throw new ArchiveOperationException("ResourceLimit");
                            metadata.Add(entry);
                        }
                        break;
                    case "ready" when opened && writer is null:
                        if (password is null && metadata.Any(e => e.Encrypted && (options.SelectedIds is null || options.SelectedIds.Contains(e.Id))))
                            throw new ArchiveOperationException("PasswordRequired");
                        // Selection IDs only make sense for this fresh metadata snapshot.
                        if (options.SelectedIds is not null && options.SelectedIds.Any(id => !ids.Contains(id)))
                            throw new ArchiveOperationException("ArchiveChanged");
                        writer = new ExtractionWriter(path, metadata, options, conflict);
                        await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream,
                            new ExtractionCommand(options.SelectedIds is null ? null : writer.EntryIds), cancellationToken);
                        process.StandardInput.Close();
                        break;
                    case "file" when writer is not null && message.EntryId is { } id:
                        await writer.BeginAsync(id, cancellationToken);
                        break;
                    case "data" when writer is not null && message.EntryId is { } id && message.Data is not null:
                        await writer.WriteAsync(id, message.Data, cancellationToken);
                        break;
                    case "fileDone" when writer is not null && message.EntryId is { } id && message.Result is { } result:
                        await writer.CompleteAsync(id, result, cancellationToken);
                        break;
                    case "completed" when writer is not null:
                        if (!writer.IsComplete) throw new ArchiveOperationException("ProtocolMismatch");
                        await process.WaitForExitAsync(cancellationToken);
                        return writer.Result(errorCode: process.ExitCode == 0 ? null : "WorkerFailed");
                    case "error": throw new ArchiveOperationException(message.ErrorCode ?? "WorkerFailed");
                    default: throw new ArchiveOperationException("ProtocolMismatch");
                }
                if (writer is not null) progress?.Report(new(writer.Completed, writer.Skipped, writer.Bytes, writer.CurrentPath));
            }
        }
        catch (OperationCanceledException) when (writer is not null) { return writer.Result(cancelled: true); }
        catch (Exception error) when (error is ArchiveOperationException or IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            string code = error switch
            {
                ArchiveOperationException operation => operation.Code,
                EndOfStreamException => "WorkerFailed",
                InvalidDataException => "ProtocolMismatch",
                UnauthorizedAccessException => "AccessDenied",
                OverflowException => "ResourceLimit",
                _ => "WriteFailed"
            };
            if (writer is null) throw new ArchiveOperationException(code);
            return writer.Result(errorCode: code);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync();
            await drain;
            if (writer is not null) await writer.DisposeAsync();
        }
    }
}
