using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AileArc.Core.Extraction;
using AileArc.Shared;
using AileArc.Core.Recovery;
using AileArc.Core.Storage;

namespace AileArc.Core.WorkCopies;

public sealed record WorkCopyRecord(int Version, string Id, string ArchivePath, ArchiveIdentity Identity,
    long EntryId, string EntryPath, string RelativePath, string? BaselineHash, DateTime CreatedUtc, string? SourceZone = null);
public enum WorkCopyState { Unchanged, Modified, Unavailable, Incomplete }
public sealed record WorkCopyStatus(WorkCopyRecord Record, string FilePath, WorkCopyState State)
{
    internal (string Id, long Length, long Written, long Changed)? Stamp { get; init; }
    internal long VerifiedAt { get; init; }
}
public sealed record WorkCopyListing(IReadOnlyList<WorkCopyRecord> Records, bool HasMore);

/// <summary>Durable working files, deliberately never automatically deleted after external applications open them.</summary>
public sealed partial class WorkCopyStore
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
            if (prepared.TryGetValue(key, out var existing) && File.Exists(FilePath(existing)))
            {
                // Reopening through the archive browser uses its indexed snapshot.
                // The retained-copy manager can still open the edited file on its own.
                using var source = File.OpenHandle(fullArchive, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (ArchiveFileIdentity.Read(source) != identity) throw new ArchiveOperationException("ArchiveChanged");
                return existing;
            }
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
            string file = FilePath(record);
            using (DirectoryGuard.Acquire(Path.GetDirectoryName(file)!, false))
            await using (var input = VerifiedFile.OpenRead(file))
                record = record with { BaselineHash = await HashAsync(input, token), SourceZone = await ReadZoneAsync(file, token) };
            // Never launch before the baseline has reached durable metadata.
            await WriteManifestAsync(record, token);
            prepared[key] = record;
            return record;
        }
        finally { gate.Release(); }
    }

    public string FilePath(WorkCopyRecord record)
    {
        if (record.Version != 1 || !Guid.TryParseExact(record.Id, "N", out _) ||
            record.Identity is null || string.IsNullOrEmpty(record.Identity.FileId) || !Path.IsPathFullyQualified(record.ArchivePath ?? "") ||
            record.EntryId is < 0 or > uint.MaxValue || record.SourceZone is not (null or "3" or "4") ||
            (record.BaselineHash is { } hash && (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))))
            throw new ArchiveOperationException("UnsafePath");
        string folder = Path.Combine(Root, record.Id);
        string relative = ArchivePathPolicy.Validate(record.RelativePath);
        if (!string.Equals(relative, Path.Combine("content", ArchivePathPolicy.Validate(record.EntryPath)), StringComparison.Ordinal))
            throw new ArchiveOperationException("UnsafePath");
        string file = Path.GetFullPath(Path.Combine(folder, relative));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveOperationException("UnsafePath");
        return file;
    }

    public Task<WorkCopyStatus> InspectAsync(WorkCopyRecord record, CancellationToken token = default) =>
        InspectCoreAsync(record, null, token);

    public Task<WorkCopyStatus> PollAsync(WorkCopyStatus previous, CancellationToken token = default) =>
        InspectCoreAsync(previous.Record, previous, token);

    private async Task<WorkCopyStatus> InspectCoreAsync(WorkCopyRecord record, WorkCopyStatus? previous, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = FilePath(record);
        if (record.BaselineHash is null) return new(record, path, WorkCopyState.Incomplete);
        try
        {
            using var guard = DirectoryGuard.Acquire(Path.GetDirectoryName(path)!, create: false);
            await using var stream = VerifiedFile.OpenRead(path);
            var stamp = VerifiedFile.TryChangeStamp(stream);
            // Only the background indicator reuses a recent hash. Explicit inspection
            // always reads the contents, even on file systems with coarse timestamps.
            if (stamp.HasValue && previous is not null && previous.Stamp == stamp && previous.FilePath == path &&
                Environment.TickCount64 - previous.VerifiedAt < 120_000) return previous;
            string current = await HashAsync(stream, token);
            return new(record, path, current == record.BaselineHash ? WorkCopyState.Unchanged : WorkCopyState.Modified)
                { Stamp = stamp, VerifiedAt = Environment.TickCount64 };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArchiveOperationException or System.ComponentModel.Win32Exception)
        { return new(record, path, WorkCopyState.Unavailable); }
    }

    public string FindCopyFolder(WorkCopyRecord record)
    {
        string folder = Path.GetDirectoryName(FilePath(record))!;
        string boundary = Path.Combine(Root, record.Id);
        while (true)
        {
            try
            {
                using var guard = DirectoryGuard.Acquire(folder, create: false);
                return folder;
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode is 2 or 3 && folder != boundary)
            { folder = Path.GetDirectoryName(folder)!; }
        }
    }

    public async Task<IReadOnlyList<WorkCopyRecord>> LoadAsync(CancellationToken token = default)
    {
        await foreach (var page in ReadPagesAsync(token: token)) return page.Records;
        return [];
    }

    public async IAsyncEnumerable<WorkCopyListing> ReadPagesAsync(int pageSize = 2000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (pageSize is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!Directory.Exists(Root)) yield break;
        using var guard = DirectoryGuard.Acquire(Root, create: false);
        var result = new List<WorkCopyRecord>();
        using var directories = Directory.EnumerateDirectories(Root).GetEnumerator();
        bool more = directories.MoveNext();
        while (more)
        {
            for (int i = 0; i < pageSize && more; i++)
            {
                token.ThrowIfCancellationRequested();
                string directory = directories.Current;
                more = directories.MoveNext();
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                try
                {
                    using var folderGuard = DirectoryGuard.Acquire(directory, create: false);
                    await using var stream = VerifiedFile.OpenRead(Path.Combine(directory, "workcopy.json"));
                    if (stream.Length > 128 * 1024) continue;
                    var record = await JsonSerializer.DeserializeAsync<WorkCopyRecord>(stream, cancellationToken: token);
                    if (record is null || record.Id != Path.GetFileName(directory)) continue;
                    _ = FilePath(record);
                    result.Add(record);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArchiveOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
            }
            yield return new(result.OrderByDescending(r => r.CreatedUtc).ToArray(), more);
            result.Clear();
        }
    }

    public Task ExportAsync(WorkCopyRecord record, string destination, CancellationToken token = default) =>
        ExportAsync(record, destination, overwrite: true, token);

    public async Task ExportAsync(WorkCopyRecord record, string destination, bool overwrite, CancellationToken token = default)
    {
        string source = FilePath(record);
        string fullDestination = Path.GetFullPath(destination);
        if (fullDestination.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullDestination.Equals(record.ArchivePath, StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("UnsafeExport");
        _ = ArchivePathPolicy.Validate(Path.GetFileName(fullDestination));
        using var sourceGuard = DirectoryGuard.Acquire(Path.GetDirectoryName(source)!, false);
        using var targetGuard = DirectoryGuard.Acquire(Path.GetDirectoryName(fullDestination)!, true);
        if (record.BaselineHash is null) throw new ArchiveOperationException("CopyUnavailable");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(fullDestination) && (File.GetAttributes(fullDestination) & FileAttributes.ReparsePoint) != 0))
            throw new ArchiveOperationException("UnsafePath");
        using var trackedTemporary = TrackedTemporaryFile.Create(Path.GetDirectoryName(fullDestination)!, "Export", outputName: Path.GetFileName(fullDestination));
        string temporary = trackedTemporary.Path;
        try
        {
            await using var input = VerifiedFile.OpenRead(source);
            await using (var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if ((ulong)input.Length > MaxFileBytes) throw new ArchiveOperationException("WorkCopyTooLarge");
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            // Preserve the working file's download mark when exporting edited content.
            string? sourceZone = StrongerZone(record.SourceZone, await ReadZoneAsync(source, token));
            if (sourceZone is not null) await WriteZoneAsync(temporary, sourceZone, token);
            token.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(fullDestination))
            {
                string? id = VerifiedFile.TryIdentity(fullDestination);
                if (id is null || id == record.Identity.FileId) throw new ArchiveOperationException("UnsafeExport");
            }
            try { File.Move(temporary, fullDestination, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(fullDestination)) { throw new ArchiveOperationException("CopyDestinationExists"); }
            // Baseline stays unchanged: exporting a copy does not update the original archive.
        }
        finally { trackedTemporary.Dispose(); }
    }

    private async Task WriteManifestAsync(WorkCopyRecord record, CancellationToken token)
    {
        string folder = Path.Combine(Root, record.Id);
        using var guard = DirectoryGuard.Acquire(folder, create: false);
        string destination = Path.Combine(folder, "workcopy.json");
        using var trackedTemporary = TrackedTemporaryFile.Create(folder, "Metadata", outputName: "workcopy.json");
        string temp = trackedTemporary.Path;
        try
        {
            await using (var output = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, record, cancellationToken: token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally { trackedTemporary.Dispose(); }
    }
    private static async Task<string> HashAsync(FileStream stream, CancellationToken token)
    {
        if ((ulong)stream.Length > MaxFileBytes) throw new ArchiveOperationException("WorkCopyTooLarge");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    public async Task RestoreSourceMarkAsync(WorkCopyRecord record, CancellationToken token = default)
    {
        if (record.SourceZone is not ("3" or "4")) return;
        string file = FilePath(record);
        using var guard = DirectoryGuard.Acquire(Path.GetDirectoryName(file)!, false);
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("UnsafePath");
        using var pin = VerifiedFile.OpenRead(file);
        string? current = await ReadZoneAsync(file, token);
        string? required = StrongerZone(record.SourceZone, current);
        if (required != current) await WriteZoneAsync(file, required!, token);
    }
    private static string? StrongerZone(string? original, string? current) =>
        original == "4" || current == "4" ? "4" : original ?? current;
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
