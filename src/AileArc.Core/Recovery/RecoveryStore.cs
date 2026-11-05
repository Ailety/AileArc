using System.ComponentModel;
using System.Text.Json;
using AileArc.Core.Extraction;
using AileArc.Core.Storage;
using AileArc.Shared;

namespace AileArc.Core.Recovery;

public sealed record RecoveryRecord(int Version, string Id, string Path, string? FileId, string Kind, DateTime CreatedUtc,
    string? ParentId = null, string? OutputName = null);
public enum RecoveryState { Recoverable, Missing, Busy, Changed, Unverified, NoRecord }
public sealed record RecoveryItem(RecoveryRecord Record, RecoveryState State, long Bytes, ArchiveIdentity? Snapshot = null,
    ArchiveIdentity? JournalSnapshot = null);
public sealed record RecoveryPage(IReadOnlyList<RecoveryItem> Items, bool HasMore);

public sealed class RecoveryStore
{
    private const int MaxJournalBytes = 128 * 1024;
    public string Root { get; }
    public static RecoveryStore Default { get; } = new();
    public RecoveryStore(string? root = null) => Root = System.IO.Path.GetFullPath(root ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AileArc", "Recovery"));
    private string Journal(string id) => System.IO.Path.Combine(Root, id + ".json");
    internal FileStream Lease(string id) => new(System.IO.Path.Combine(Root, id + ".lock"), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    private static bool IsFileId(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9A-F]{8}:[0-9A-F]{16}\z");
    private static bool Valid(RecoveryRecord r) => r.Version is 1 or 2 && Guid.TryParseExact(r.Id, "N", out _) &&
        !string.IsNullOrEmpty(r.Path) && System.IO.Path.IsPathFullyQualified(r.Path) &&
        System.IO.Path.GetFileName(r.Path) == $".ailearc-{r.Id}.partial" &&
        !string.IsNullOrWhiteSpace(r.Kind) && (r.FileId is null || IsFileId(r.FileId)) &&
        (r.Version == 1 ? r.ParentId is null && r.OutputName is null : r.ParentId is not null && IsFileId(r.ParentId)) &&
        (r.OutputName is null || (r.OutputName.Length is > 0 and <= 255 && r.OutputName is not ("." or "..") &&
            r.OutputName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0));

    internal void Save(RecoveryRecord record, bool replace)
    {
        if (!Valid(record)) throw new ArchiveOperationException("RecoveryUnverified");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        // Leave room to replace the initial null file ID with its final value.
        if (bytes.Length + (record.FileId is null ? 32 : 0) > MaxJournalBytes) throw new ArchiveOperationException("ResourceLimit");
        using var guard = DirectoryGuard.Acquire(Root, create: true);
        string temporary = Journal(record.Id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var journal = VerifiedFile.CreateStaging(temporary);
        try
        {
            journal.Stream.Write(bytes);
            journal.Stream.Flush(flushToDisk: true);
            journal.RenameTo(Journal(record.Id), replace);
        }
        catch { journal.DeleteOnClose(); throw; }
    }
    private static RecoveryRecord Read(VerifiedFile file, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArchiveOperationException("UnsafePath");
        if (file.Stream.Length > MaxJournalBytes) throw new ArchiveOperationException("ResourceLimit");
        var record = JsonSerializer.Deserialize<RecoveryRecord>(file.Stream) ?? throw new ArchiveOperationException("RecoveryUnverified");
        if (!Valid(record) || record.Id != id) throw new ArchiveOperationException("RecoveryUnverified");
        return record;
    }
    public IReadOnlyList<RecoveryItem> List() => ReadPages().FirstOrDefault()?.Items ?? [];

    public IEnumerable<RecoveryPage> ReadPages(int pageSize = 2000, CancellationToken token = default)
    {
        if (pageSize is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        token.ThrowIfCancellationRequested();
        using var guard = OpenRoot();
        if (guard is null) yield break;
        using var paths = Directory.EnumerateFiles(Root, "*.json").GetEnumerator();
        bool more = paths.MoveNext();
        while (more)
        {
            var items = new List<RecoveryItem>();
            for (int i = 0; i < pageSize && more; i++)
            {
                token.ThrowIfCancellationRequested();
                string id = System.IO.Path.GetFileNameWithoutExtension(paths.Current);
                more = paths.MoveNext();
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var item = CheckJournal(id, false, token);
                if (item.State != RecoveryState.NoRecord) items.Add(item);
            }
            yield return new(items.OrderByDescending(i => i.Record.CreatedUtc).ToArray(), more);
        }
    }

    private DirectoryGuard? OpenRoot()
    {
        try { return DirectoryGuard.Acquire(Root, false); }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) { return null; }
    }

    public RecoveryState Cleanup(string id, CancellationToken token = default) => Cleanup(id, null, token);
    public RecoveryState Cleanup(RecoveryItem confirmed, CancellationToken token = default) => Cleanup(confirmed.Record.Id, confirmed, token);

    private RecoveryState Cleanup(string id, RecoveryItem? confirmed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArchiveOperationException("UnsafePath");
        using var guard = OpenRoot();
        if (guard is null) return confirmed is null ? RecoveryState.NoRecord : RecoveryState.Unverified;
        return CheckJournal(id, true, token, confirmed).State;
    }

    private RecoveryItem CheckJournal(string id, bool remove, CancellationToken token, RecoveryItem? confirmed = null)
    {
        // Do not open an active task's journal: it may be replacing its initial
        // registration. Busy and unreadable rows carry no untrusted target path.
        var unknown = new RecoveryRecord(0, id, "", null, "Unknown", default);
        try
        {
            using var lease = InspectionLease(id);
            if (lease is null) return new(unknown, RecoveryState.Busy, 0);
            return WithJournal(id, remove, token, confirmed?.Record, confirmed);
        }
        catch (Exception e) when (IsInspectionError(e)) { return new(unknown, RecoveryState.Unverified, 0); }
    }

    private IDisposable? InspectionLease(string id)
    {
        try { return Lease(id); }
        catch (IOException e) when ((e.HResult & 0xffff) is 80 or 183)
        {
            // After a power loss an empty lease name can survive without its
            // owning handle. Lock that exact regular file; never delete it.
            VerifiedFile existing;
            try { existing = new VerifiedFile(System.IO.Path.Combine(Root, id + ".lock")); }
            catch (Win32Exception error) when (error.NativeErrorCode is 32 or 33 or 5) { return null; }
            catch (Win32Exception error) when (error.NativeErrorCode == 2) { return Lease(id); }
            try
            {
                if (existing.Stream.Length != 0) throw new ArchiveOperationException("RecoveryUnverified");
                return existing;
            }
            catch { existing.Dispose(); throw; }
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }

    internal RecoveryState CleanupOwned(RecoveryRecord record)
    {
        using var guard = DirectoryGuard.Acquire(Root, false);
        try { return WithJournal(record.Id, true, default, record).State; }
        catch (Exception e) when (IsInspectionError(e)) { return RecoveryState.Unverified; }
    }

    private RecoveryItem WithJournal(string id, bool remove, CancellationToken token, RecoveryRecord? expected = null, RecoveryItem? confirmed = null)
    {
        VerifiedFile journal;
        try { journal = new VerifiedFile(Journal(id)); }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
        { return new(new(0, id, "", null, "Unknown", default), RecoveryState.NoRecord, 0); }
        using (journal)
        {
            var record = Read(journal, id);
            var registration = ArchiveFileIdentity.Read(journal.Stream.SafeFileHandle);
            if ((expected is not null && expected != record) || (confirmed is not null && registration != confirmed.JournalSnapshot))
                return new(record, RecoveryState.Changed, 0);
            var item = CheckTarget(record, remove, token, confirmed);
            if (remove && item.State is RecoveryState.Recoverable or RecoveryState.Missing) journal.DeleteOnClose();
            return item with { JournalSnapshot = registration };
        }
    }

    private static RecoveryItem CheckTarget(RecoveryRecord record, bool remove, CancellationToken token, RecoveryItem? confirmed)
    {
        if (record.FileId is null) return new(record, RecoveryState.Unverified, 0);
        try
        {
            using var guard = DirectoryGuard.Acquire(System.IO.Path.GetDirectoryName(record.Path)!, false);
            if (record.ParentId is not null ? guard.FileIdentity != record.ParentId :
                !guard.FileIdentity.AsSpan(0, 8).SequenceEqual(record.FileId.AsSpan(0, 8))) return new(record, RecoveryState.Changed, 0);
            VerifiedFile file;
            try { file = new VerifiedFile(record.Path); }
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
            {
                token.ThrowIfCancellationRequested();
                if (record.ParentId is null) return new(record, RecoveryState.Unverified, 0);
                return new(record, confirmed is null || confirmed.State == RecoveryState.Missing ? RecoveryState.Missing : RecoveryState.Changed, 0);
            }
            using (file)
            {
                var identity = ArchiveFileIdentity.Read(file.Stream.SafeFileHandle);
                if (file.Identity != record.FileId || (confirmed is not null &&
                    (confirmed.State != RecoveryState.Recoverable || confirmed.Snapshot != identity))) return new(record, RecoveryState.Changed, 0);
                token.ThrowIfCancellationRequested();
                if (remove) file.DeleteOnClose();
                return new(record, RecoveryState.Recoverable, identity.Length, identity);
            }
        }
        catch (Exception e) when (IsInspectionError(e)) { return new(record, RecoveryState.Changed, 0); }
    }

    private static bool IsInspectionError(Exception e) =>
        e is IOException or UnauthorizedAccessException or Win32Exception or ArchiveOperationException or JsonException or ArgumentException;
}

/// <summary>Owns a durable journal and a live lease for exactly one random staging file.</summary>
public sealed class TrackedTemporaryFile : IDisposable
{
    private readonly RecoveryStore store;
    private readonly FileStream lease;
    private RecoveryRecord record;
    public string Path => record.Path;
    internal string? FileId => record.FileId;
    public string Id => record.Id;
    private bool disposed;
    private TrackedTemporaryFile(RecoveryStore store, FileStream lease, RecoveryRecord record)
    { this.store = store; this.lease = lease; this.record = record; }
    public static TrackedTemporaryFile Create(string directory, string kind, RecoveryStore? store = null, string? outputName = null)
    {
        store ??= RecoveryStore.Default;
        using var rootGuard = DirectoryGuard.Acquire(store.Root, true);
        using var targetGuard = DirectoryGuard.Acquire(directory, false);
        string id = Guid.NewGuid().ToString("N");
        var lease = store.Lease(id);
        var record = new RecoveryRecord(2, id, System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), $".ailearc-{id}.partial"),
            null, kind, DateTime.UtcNow, targetGuard.FileIdentity, outputName);
        try { store.Save(record, replace: false); }
        catch { lease.Dispose(); throw; }
        var tracked = new TrackedTemporaryFile(store, lease, record);
        try
        {
            using var file = new FileStream(record.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            tracked.record = record with { FileId = VerifiedFile.IdentityOf(file.SafeFileHandle) };
            store.Save(tracked.record, replace: true);
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
