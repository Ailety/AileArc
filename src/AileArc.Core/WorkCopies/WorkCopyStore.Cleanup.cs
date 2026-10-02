using System.Security.Cryptography;
using System.Text.Json;
using AileArc.Core.Extraction;
using AileArc.Core.Storage;

namespace AileArc.Core.WorkCopies;

public sealed partial class WorkCopyStore
{
    /// <summary>Explicit cleanup only: re-read the manifest and hash the locked file immediately before deletion.</summary>
    public async Task RemoveUnchangedAsync(WorkCopyRecord record, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        var directories = new List<string>();
        try
        {
            string file = FilePath(record);
            string folder = Path.Combine(Root, record.Id);
            string manifest = Path.Combine(folder, "workcopy.json");
            for (string? current = Path.GetDirectoryName(file); current is not null && current.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
                current = Path.GetDirectoryName(current)) directories.Add(current);
            using (var guard = DirectoryGuard.Acquire(Path.GetDirectoryName(file)!, false))
            using (var metadata = new VerifiedFile(manifest))
            using (var payload = new VerifiedFile(file))
            {
                if (metadata.Stream.Length > 128 * 1024) throw new ArchiveOperationException("RecoveryUnverified");
                var actual = await JsonSerializer.DeserializeAsync<WorkCopyRecord>(metadata.Stream, cancellationToken: token);
                if (actual != record || actual.BaselineHash is null) throw new ArchiveOperationException("CopyCleanupRefused");
                if ((ulong)payload.Stream.Length > MaxFileBytes) throw new ArchiveOperationException("CopyCleanupRefused");
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(payload.Stream, token));
                if (hash != actual.BaselineHash) throw new ArchiveOperationException("CopyCleanupRefused");
                // No recursive delete: refuse unexpected editor backups, partials or linked directories.
                foreach (string directory in directories)
                    foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                            !directories.Contains(child, StringComparer.OrdinalIgnoreCase) &&
                            !child.Equals(file, StringComparison.OrdinalIgnoreCase) && !child.Equals(manifest, StringComparison.OrdinalIgnoreCase))
                            throw new ArchiveOperationException("CopyCleanupExtraFiles");
                    }
                token.ThrowIfCancellationRequested();
                payload.DeleteOnClose();
                metadata.DeleteOnClose();
            }
            foreach (string key in prepared.Where(p => p.Value.Id == record.Id).Select(p => p.Key).ToArray()) prepared.Remove(key);
            foreach (string directory in directories)
                try { Directory.Delete(directory, recursive: false); }
                catch (IOException) { /* Newly added files are retained. */ }
        }
        finally { gate.Release(); }
    }
}
