using System.ComponentModel;
using System.Text.Json;
using AileArc.Core.Extraction;
using AileArc.Core.Storage;

namespace AileArc.Core.Recovery;

public sealed record RecoveryRecord(int Version, string Id, string Path, string? FileId, string Kind, DateTime CreatedUtc);
public enum RecoveryState { Recoverable, Missing, Busy, Changed, Unverified }
public sealed record RecoveryItem(RecoveryRecord Record, RecoveryState State, long Bytes);

public sealed class RecoveryStore
{
    public string Root { get; }
    public static RecoveryStore Default { get; } = new();
    public RecoveryStore(string? root = null) => Root = System.IO.Path.GetFullPath(root ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AileArc", "Recovery"));
    private string Journal(string id) => System.IO.Path.Combine(Root, id + ".json");
    internal FileStream Lease(string id) => new(System.IO.Path.Combine(Root, id + ".lock"), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    private static bool Valid(RecoveryRecord r) => r.Version == 1 && Guid.TryParseExact(r.Id, "N", out _) &&
        !string.IsNullOrEmpty(r.Path) && System.IO.Path.IsPathFullyQualified(r.Path) &&
        System.IO.Path.GetFileName(r.Path) == $".ailearc-{r.Id}.partial" &&
        (r.FileId is null || System.Text.RegularExpressions.Regex.IsMatch(r.FileId, "^[0-9A-F]{8}:[0-9A-F]{16}$"));

    internal RecoveryRecord Register(string id, string path, string kind, string? identity)
    {
        var record = new RecoveryRecord(1, id, path, identity, kind, DateTime.UtcNow);
        Save(record);
        return record;
    }
    private void Save(RecoveryRecord record)
    {
        using var guard = DirectoryGuard.Acquire(Root, create: true);
        string temporary = Journal(record.Id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(record));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Journal(record.Id), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private RecoveryRecord Read(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArchiveOperationException("UnsafePath");
        using var file = new VerifiedFile(Journal(id));
        if (file.Stream.Length > 128 * 1024) throw new ArchiveOperationException("ResourceLimit");
        var record = JsonSerializer.Deserialize<RecoveryRecord>(file.Stream) ?? throw new ArchiveOperationException("RecoveryUnverified");
        if (!Valid(record) || record.Id != id) throw new ArchiveOperationException("RecoveryUnverified");
        return record;
    }
    public IReadOnlyList<RecoveryItem> List()
    {
        if (!Directory.Exists(Root)) return [];
        using var guard = DirectoryGuard.Acquire(Root, create: false);
        var items = new List<RecoveryItem>();
        foreach (string path in Directory.EnumerateFiles(Root, "*.json").Take(2000))
        {
            string id = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var record = Read(id);
                try { using var lease = Lease(id); items.Add(Inspect(record)); }
                catch (IOException) { items.Add(new(record, RecoveryState.Busy, 0)); }
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or ArchiveOperationException or JsonException or ArgumentException)
            { /* Invalid journals never authorize touching an external path. */ }
        }
        return items.OrderByDescending(i => i.Record.CreatedUtc).ToArray();
    }
    private static RecoveryItem Inspect(RecoveryRecord record)
    {
        if (record.FileId is null) return new(record, RecoveryState.Unverified, 0);
        try
        {
            using var guard = DirectoryGuard.Acquire(System.IO.Path.GetDirectoryName(record.Path)!, false);
            if (!guard.FileIdentity.AsSpan(0, 8).SequenceEqual(record.FileId.AsSpan(0, 8))) return new(record, RecoveryState.Changed, 0);
            try
            {
                using var file = new VerifiedFile(record.Path);
                return new(record, file.Identity == record.FileId ? RecoveryState.Recoverable : RecoveryState.Changed, file.Stream.Length);
            }
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) { return new(record, RecoveryState.Missing, 0); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArchiveOperationException)
        { return new(record, RecoveryState.Changed, 0); }
    }
    public RecoveryState Cleanup(string id, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var guard = DirectoryGuard.Acquire(Root, false);
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArchiveOperationException("UnsafePath");
        FileStream lease;
        try { lease = Lease(id); }
        catch (IOException) { return RecoveryState.Busy; }
        using (lease) return CleanupOwned(Read(id), token);
    }
    internal RecoveryState CleanupOwned(RecoveryRecord record, CancellationToken token = default)
    {
        using var rootGuard = DirectoryGuard.Acquire(Root, false);
        if (!Valid(record) || record.FileId is null) return RecoveryState.Unverified;
        try
        {
            using var targetGuard = DirectoryGuard.Acquire(System.IO.Path.GetDirectoryName(record.Path)!, false);
            if (!targetGuard.FileIdentity.AsSpan(0, 8).SequenceEqual(record.FileId.AsSpan(0, 8))) return RecoveryState.Changed;
            try
            {
                using var target = new VerifiedFile(record.Path);
                if (target.Identity != record.FileId) return RecoveryState.Changed;
                token.ThrowIfCancellationRequested();
                target.DeleteOnClose();
            }
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) { token.ThrowIfCancellationRequested(); Forget(record.Id); return RecoveryState.Missing; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArchiveOperationException)
        { return RecoveryState.Changed; }
        Forget(record.Id);
        return RecoveryState.Recoverable;
    }
    private void Forget(string id)
    {
        using var file = new VerifiedFile(Journal(id));
        file.DeleteOnClose();
    }
}

/// <summary>Owns a durable journal and a live lease for exactly one random staging file.</summary>
public sealed class TrackedTemporaryFile : IDisposable
{
    private readonly RecoveryStore store;
    private readonly FileStream lease;
    private RecoveryRecord record;
    public string Path => record.Path;
    public string Id => record.Id;
    private bool disposed;
    private TrackedTemporaryFile(RecoveryStore store, FileStream lease, RecoveryRecord record)
    { this.store = store; this.lease = lease; this.record = record; }
    public static TrackedTemporaryFile Create(string directory, string kind, RecoveryStore? store = null)
    {
        store ??= RecoveryStore.Default;
        using var rootGuard = DirectoryGuard.Acquire(store.Root, true);
        using var targetGuard = DirectoryGuard.Acquire(directory, false);
        string id = Guid.NewGuid().ToString("N");
        var lease = store.Lease(id);
        RecoveryRecord record;
        try { record = store.Register(id, System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), $".ailearc-{id}.partial"), kind, null); }
        catch { lease.Dispose(); throw; }
        var tracked = new TrackedTemporaryFile(store, lease, record);
        try
        {
            using var file = new FileStream(record.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            tracked.record = record with { FileId = VerifiedFile.IdentityOf(file.SafeFileHandle) };
            tracked.record = store.Register(id, record.Path, kind, tracked.record.FileId);
            return tracked;
        }
        catch { tracked.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { _ = store.CleanupOwned(record); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArchiveOperationException) { }
        finally { lease.Dispose(); }
    }
}
