using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AileArc.Core.Extraction;
using AileArc.Shared;

namespace AileArc.Core.WorkCopies;

public sealed record WorkCopyRecord(int Version, string Id, string ArchivePath, ArchiveIdentity Identity,
    long EntryId, string EntryPath, string RelativePath, string? BaselineHash, DateTime CreatedUtc, string? SourceZone = null);
public enum WorkCopyState { Unchanged, Modified, Unavailable, Incomplete }
public sealed record WorkCopyStatus(WorkCopyRecord Record, string FilePath, WorkCopyState State);

/// <summary>Durable working files, deliberately never automatically deleted after external applications open them.</summary>
public sealed class WorkCopyStore
{
    public const ulong MaxFileBytes = 256UL * 1024 * 1024;
    public string Root { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, WorkCopyRecord> prepared = new(StringComparer.Ordinal);
    public WorkCopyStore(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AileArc", "WorkCopies"));

    public async Task<WorkCopyRecord> PrepareAsync(string archivePath, ArchiveIdentity identity, ArchiveEntry entry,
        Func<string, CancellationToken, Task<ExtractionResult>> extract, CancellationToken token = default)
    {
        if (entry.IsDirectory || entry.IsLink) throw new ArchiveOperationException("LinksNotSupported");
        if (entry.Size > MaxFileBytes) throw new ArchiveOperationException("WorkCopyTooLarge");
        string relative = Path.Combine("content", ArchivePathPolicy.Validate(entry.Path));
        string fullArchive = Path.GetFullPath(archivePath);
        string key = JsonSerializer.Serialize(new { Path = fullArchive.ToUpperInvariant(), identity, entry.Id, EntryPath = entry.Path });
        await gate.WaitAsync(token);
        try
        {
            if (prepared.TryGetValue(key, out var existing) && File.Exists(FilePath(existing))) return existing;
            using var rootGuard = DirectoryGuard.Acquire(Root, create: true);
            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(Root, id);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).Create(security);
            var record = new WorkCopyRecord(1, id, fullArchive, identity, entry.Id, entry.Path, relative, null, DateTime.UtcNow);
            await WriteManifestAsync(record, token);
            var result = await extract(Path.Combine(folder, "content"), token);
            if (result.Cancelled) throw new OperationCanceledException(token);
            if (result.Failures.Count > 0) throw new ArchiveOperationException(result.Failures[0].Code);
            if (result.Completed != 1) throw new ArchiveOperationException("OpenFailed");
            string hash = await HashAsync(FilePath(record), token);
            record = record with { BaselineHash = hash, SourceZone = await ReadZoneAsync(FilePath(record), token) };
            // Never launch before the baseline has reached durable metadata.
            await WriteManifestAsync(record, token);
            prepared[key] = record;
            return record;
        }
        finally { gate.Release(); }
    }

    public string FilePath(WorkCopyRecord record)
    {
        if (record.Version != 1 || !Guid.TryParseExact(record.Id, "N", out _)) throw new ArchiveOperationException("UnsafePath");
        string folder = Path.Combine(Root, record.Id);
        string relative = ArchivePathPolicy.Validate(record.RelativePath);
        string file = Path.GetFullPath(Path.Combine(folder, relative));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveOperationException("UnsafePath");
        return file;
    }

    public async Task<WorkCopyStatus> InspectAsync(WorkCopyRecord record, CancellationToken token = default)
    {
        string path = FilePath(record);
        if (record.BaselineHash is null) return new(record, path, WorkCopyState.Incomplete);
        try
        {
            using var guard = DirectoryGuard.Acquire(Path.GetDirectoryName(path)!, create: false);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("UnsafePath");
            string current = await HashAsync(path, token);
            return new(record, path, current == record.BaselineHash ? WorkCopyState.Unchanged : WorkCopyState.Modified);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArchiveOperationException or System.ComponentModel.Win32Exception)
        { return new(record, path, WorkCopyState.Unavailable); }
    }

    public async Task<IReadOnlyList<WorkCopyRecord>> LoadAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(Root)) return [];
        using var guard = DirectoryGuard.Acquire(Root, create: false);
        var result = new List<WorkCopyRecord>();
        foreach (string directory in Directory.EnumerateDirectories(Root).Take(2000))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            string manifest = Path.Combine(directory, "workcopy.json");
            try
            {
                using var folderGuard = DirectoryGuard.Acquire(directory, create: false);
                if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifest).Length > 128 * 1024) continue;
                var record = JsonSerializer.Deserialize<WorkCopyRecord>(await File.ReadAllTextAsync(manifest, token));
                if (record is null || record.Id != Path.GetFileName(directory)) continue;
                _ = FilePath(record);
                result.Add(record);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArchiveOperationException) { }
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToArray();
    }

    public async Task ExportAsync(WorkCopyRecord record, string destination, CancellationToken token = default)
    {
        string source = FilePath(record);
        string fullDestination = Path.GetFullPath(destination);
        if (fullDestination.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullDestination.Equals(record.ArchivePath, StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("UnsafeExport");
        _ = ArchivePathPolicy.Validate(Path.GetFileName(fullDestination));
        using var sourceGuard = DirectoryGuard.Acquire(Path.GetDirectoryName(source)!, false);
        using var targetGuard = DirectoryGuard.Acquire(Path.GetDirectoryName(fullDestination)!, true);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(fullDestination) && (File.GetAttributes(fullDestination) & FileAttributes.ReparsePoint) != 0))
            throw new ArchiveOperationException("UnsafePath");
        string temporary = Path.Combine(Path.GetDirectoryName(fullDestination)!, $".ailearc-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if ((ulong)input.Length > MaxFileBytes) throw new ArchiveOperationException("WorkCopyTooLarge");
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            // Preserve the working file's download mark when exporting edited content.
            string? sourceZone = record.SourceZone is "3" or "4" ? record.SourceZone : await ReadZoneAsync(source, token);
            if (sourceZone is not null) await WriteZoneAsync(temporary, sourceZone, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullDestination, overwrite: true);
            // Baseline stays unchanged: exporting a copy does not update the original archive.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task WriteManifestAsync(WorkCopyRecord record, CancellationToken token)
    {
        string folder = Path.Combine(Root, record.Id);
        using var guard = DirectoryGuard.Acquire(folder, create: false);
        string destination = Path.Combine(folder, "workcopy.json");
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, record, cancellationToken: token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task<string> HashAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if ((ulong)stream.Length > MaxFileBytes) throw new ArchiveOperationException("WorkCopyTooLarge");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    public async Task RestoreSourceMarkAsync(WorkCopyRecord record, CancellationToken token = default)
    {
        if (record.SourceZone is not ("3" or "4")) return;
        string file = FilePath(record);
        using var guard = DirectoryGuard.Acquire(Path.GetDirectoryName(file)!, false);
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("UnsafePath");
        await WriteZoneAsync(file, record.SourceZone, token);
    }
    private static async Task<string?> ReadZoneAsync(string source, CancellationToken token)
    {
        try
        {
            using var zone = new FileStream(source + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read);
            if (zone.Length > 16384) throw new ArchiveOperationException("InvalidSourceZone");
            using var reader = new StreamReader(zone);
            string data = await reader.ReadToEndAsync(token);
            return data.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line is "ZoneId=3" or "ZoneId=4")?[7..];
        }
        catch (FileNotFoundException) { return null; }
    }
    private static Task WriteZoneAsync(string file, string zone, CancellationToken token) =>
        File.WriteAllTextAsync(file + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId={zone}\r\n", Encoding.ASCII, token);
}
