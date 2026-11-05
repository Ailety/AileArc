using System.ComponentModel;
using AileArc.Core.Compression;
using AileArc.Core.Extraction;
using AileArc.Shared;
using AileArc.Core.Recovery;
using AileArc.Core.Storage;

namespace AileArc.Core;

public sealed partial class ArchiveWorkerClient
{
    private static readonly object creationCommit = new();
    public async Task<CompressionResult> CreateAsync(CompressionOptions options, IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(options.Destination);
        var target = options.ConfirmedDestination ?? CompressionDestination.Capture(destination);
        if (options.ConfirmedDestination is not null) target.Check(destination);
        using var plan = CompressionPlan.Build(options, cancellationToken);
        if (target.Existing is not null && plan.Items.Any(item => item.Snapshot.FileId == target.Existing.FileId))
            throw new ArchiveOperationException("DestinationInsideSource");
        if (target.Existing is not null && !options.Overwrite) throw new ArchiveOperationException("DestinationConflict");
        using var destinationGuard = DirectoryGuard.Acquire(Path.GetDirectoryName(destination)!, create: true);
        using var trackedTemporary = TrackedTemporaryFile.Create(Path.GetDirectoryName(destination)!, "Compress");
        string temporary = trackedTemporary.Path;
        using var stage = VerifiedFile.OpenStaging(temporary);
        var emptyStage = ArchiveFileIdentity.Read(stage.Stream.SafeFileHandle);
        if (emptyStage.FileId != trackedTemporary.FileId || emptyStage.Length != 0) throw new ArchiveOperationException("CompressionOutputChanged");
        try
        {
            using var process = StartWorker();
            using var job = WorkerJob.Attach(process);
            await using var pipe = new WorkerPipe(process, job, idleTimeout, cancellationToken);
            bool verified = false;
            progress?.Report(new("compressing", 0, plan.Total));
            await pipe.WriteAsync(new ScanRequest(ArchiveProtocol.Version, "", "create",
                string.IsNullOrEmpty(options.Password) ? null : options.Password, options.ByteLimit,
                CompressionFormat: options.Format, CompressionPreset: options.Preset, CompressionCount: plan.Items.Count, EncryptNames: options.EncryptNames,
                ExpectedIdentity: emptyStage, OutputHandle: stage.DuplicateReadWriteTo(process)));
            var batch = new List<CompressionItem>();
            int characters = 0;
            foreach (var item in plan.Items)
            {
                batch.Add(item); characters += item.SourcePath.Length + item.EntryPath.Length;
                if (batch.Count == 32 || characters > 32000)
                {
                    await pipe.WriteAsync(new CompressionBatch(batch.ToArray()));
                    batch.Clear(); characters = 0;
                }
            }
            if (batch.Count > 0) await pipe.WriteAsync(new CompressionBatch(batch.ToArray()));
            process.StandardInput.Close();
            while (true)
            {
                var message = await pipe.ReadAsync();
                switch (message.Kind)
                {
                    case "activity" when verified: break;
                    case "compressing": progress?.Report(new("compressing", message.CompletedBytes ?? 0, message.TotalBytes ?? plan.Total)); break;
                    case "verifying": verified = true; progress?.Report(new("verifying", plan.Total, plan.Total)); break;
                    case "error":
                        string? affected = message.EntryId is long index && index >= 0 && index < plan.Items.Count
                            ? plan.Items[(int)index].SourcePath : null;
                        throw new ArchiveOperationException(message.ErrorCode ?? "CompressionFailed", affected);
                    case "completed" when verified:
                        await pipe.WaitForExitAsync();
                        if (process.ExitCode != 0) throw new ArchiveOperationException("WorkerFailed");
                        if (message.Identity is null) throw new ArchiveOperationException("ProtocolMismatch");
                        plan.ValidateForCommit(cancellationToken);
                        lock (creationCommit)
                        {
                            if (ArchiveFileIdentity.Read(stage.Stream.SafeFileHandle) != message.Identity)
                                throw new ArchiveOperationException("CompressionOutputChanged");
                            target.Check(destination);
                            cancellationToken.ThrowIfCancellationRequested();
                            try { stage.RenameTo(destination, target.Existing is not null); }
                            catch (Win32Exception e) when (e.NativeErrorCode is 80 or 183) { throw new ArchiveOperationException("CompressionTargetChanged"); }
                            catch (Win32Exception e) when (e.NativeErrorCode is 5 or 32 or 33) { throw new ArchiveOperationException("CompressionTargetUnavailable"); }
                            stage.Dispose();
                        }
                        return new(destination, plan.Items.Count, plan.Total);
                    default: throw new ArchiveOperationException("ProtocolMismatch");
                }
            }
        }
        catch (EndOfStreamException) { throw new ArchiveOperationException("WorkerFailed"); }
        catch (InvalidDataException) { throw new ArchiveOperationException("ProtocolMismatch"); }
    }
}
