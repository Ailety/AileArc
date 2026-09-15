using System.ComponentModel;
using System.Diagnostics;
using AileArc.Core.Compression;
using AileArc.Core.Extraction;
using AileArc.Shared;

namespace AileArc.Core;

public sealed partial class ArchiveWorkerClient
{
    public async Task<CompressionResult> CreateAsync(CompressionOptions options, IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var plan = CompressionPlan.Build(options, cancellationToken);
        string destination = Path.GetFullPath(options.Destination);
        using var destinationGuard = DirectoryGuard.Acquire(Path.GetDirectoryName(destination)!, create: true);
        if (Directory.Exists(destination) || (File.Exists(destination) && (!options.Overwrite || (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)))
            throw new ArchiveOperationException("DestinationConflict");
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".ailearc-{Guid.NewGuid():N}.partial");
        using (File.Create(temporary)) { }
        try
        {
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
            Task drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            bool verified = false;
            try
            {
                progress?.Report(new("compressing", 0, plan.Total));
                await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream, new ScanRequest(ArchiveProtocol.Version, temporary, "create",
                    string.IsNullOrEmpty(options.Password) ? null : options.Password, options.ByteLimit,
                    CompressionFormat: options.Format, CompressionPreset: options.Preset, CompressionCount: plan.Items.Count, EncryptNames: options.EncryptNames), cancellationToken);
                var batch = new List<CompressionItem>();
                int characters = 0;
                foreach (var item in plan.Items)
                {
                    batch.Add(item); characters += item.SourcePath.Length + item.EntryPath.Length;
                    if (batch.Count == 32 || characters > 32000)
                    {
                        await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream, new CompressionBatch(batch.ToArray()), cancellationToken);
                        batch.Clear(); characters = 0;
                    }
                }
                if (batch.Count > 0) await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream, new CompressionBatch(batch.ToArray()), cancellationToken);
                process.StandardInput.Close();
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                    ScanMessage message;
                    try { message = await ArchiveProtocol.ReadAsync<ScanMessage>(process.StandardOutput.BaseStream, idle.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ArchiveOperationException("TimedOut"); }
                    switch (message.Kind)
                    {
                        case "compressing": progress?.Report(new("compressing", message.CompletedBytes ?? 0, message.TotalBytes ?? plan.Total)); break;
                        case "verifying": verified = true; progress?.Report(new("verifying", plan.Total, plan.Total)); break;
                        case "error": throw new ArchiveOperationException(message.ErrorCode ?? "CompressionFailed");
                        case "completed" when verified:
                            await process.WaitForExitAsync(cancellationToken);
                            if (process.ExitCode != 0) throw new ArchiveOperationException("WorkerFailed");
                            cancellationToken.ThrowIfCancellationRequested();
                            File.Move(temporary, destination, overwrite: options.Overwrite);
                            return new(destination, plan.Items.Count, plan.Total);
                        default: throw new ArchiveOperationException("ProtocolMismatch");
                    }
                }
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                await process.WaitForExitAsync();
                await drain;
            }
        }
        catch (EndOfStreamException) { throw new ArchiveOperationException("WorkerFailed"); }
        catch (InvalidDataException) { throw new ArchiveOperationException("ProtocolMismatch"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
