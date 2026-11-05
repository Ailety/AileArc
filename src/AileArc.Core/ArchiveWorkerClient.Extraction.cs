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
        if (options.SelectedIds?.Length > 100_000) throw new ArchiveOperationException("ResourceLimit");
        var selectedIds = options.SelectedIds?.ToHashSet();
        using var process = StartWorker();
        using var job = WorkerJob.Attach(process);
        await using var pipe = new WorkerPipe(process, job, idleTimeout, cancellationToken);
        ExtractionWriter? writer = null;
        try
        {
            await pipe.WriteAsync(new ScanRequest(ArchiveProtocol.Version, Path.GetFullPath(path), "extract", password, options.ByteLimit, options.NameCodePage, ExpectedIdentity: options.ExpectedIdentity));
            var metadata = new List<ArchiveEntry>();
            var budget = new MetadataBudget();
            bool opened = false;
            while (true)
            {
                var message = await pipe.ReadAsync();
                switch (message.Kind)
                {
                    case "activity" when !opened || writer is not null: break;
                    case "opened" when !opened:
                        if (message.Identity is null) throw new ArchiveOperationException("ProtocolMismatch");
                        if (options.ExpectedIdentity is not null && options.ExpectedIdentity != message.Identity)
                            throw new ArchiveOperationException("ArchiveChanged");
                        opened = true;
                        break;
                    case "entries" when opened && writer is null && message.Entries is { Length: > 0 }:
                        foreach (var entry in message.Entries)
                        {
                            budget.Accept(entry);
                            if (selectedIds is null || selectedIds.Contains(entry.Id)) metadata.Add(entry);
                        }
                        break;
                    case "ready" when opened && writer is null:
                        // Selection IDs only make sense for this fresh metadata snapshot.
                        if (selectedIds is not null && metadata.Count != selectedIds.Count)
                            throw new ArchiveOperationException("ArchiveChanged");
                        if (password is null && metadata.Any(e => e.Encrypted))
                            throw new ArchiveOperationException("PasswordRequired");
                        writer = new ExtractionWriter(path, metadata, options, conflict, cancellationToken);
                        await pipe.WriteAsync(new ExtractionCommand(options.SelectedIds is null ? null : writer.EntryIds));
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
                        await pipe.WaitForExitAsync();
                        return writer.Result(errorCode: process.ExitCode == 0 ? null : "WorkerFailed");
                    case "error": throw new ArchiveOperationException(message.ErrorCode ?? "WorkerFailed");
                    default: throw new ArchiveOperationException("ProtocolMismatch");
                }
                if (writer is not null) progress?.Report(new(writer.Completed, writer.Skipped, writer.Bytes, writer.CurrentPath));
            }
        }
        catch (OperationCanceledException) when (writer is not null) { return writer.Result(cancelled: true); }
        catch (Exception error) when (error is ArchiveOperationException or IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or OverflowException)
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
            if (writer is not null) await writer.DisposeAsync();
        }
    }
}
