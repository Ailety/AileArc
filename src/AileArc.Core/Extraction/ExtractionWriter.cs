using System.Runtime.InteropServices;
using AileArc.Shared;

namespace AileArc.Core.Extraction;

public enum ConflictChoice { Rename, Skip, Replace, Cancel }
public sealed record ConflictDecision(ConflictChoice Choice, bool ApplyToAll = false);
public sealed record ExtractionOptions(string Destination, bool Smart = true, ConflictChoice? ConflictPolicy = null,
    ulong ByteLimit = 4UL * 1024 * 1024 * 1024, long[]? SelectedIds = null, ArchiveIdentity? ExpectedIdentity = null, int NameCodePage = 0);
public sealed record ExtractionFailure(string Path, string Code);
public sealed record ExtractionProgress(int Completed, int Skipped, ulong Bytes, string? CurrentPath);
public sealed record ExtractionResult(string Destination, int Completed, int Skipped, IReadOnlyList<ExtractionFailure> Failures, bool Cancelled);

internal sealed class ExtractionWriter : IAsyncDisposable
{
    private readonly Dictionary<long, ArchiveEntry> entries;
    private readonly ExtractionOptions options;
    private readonly Func<string, CancellationToken, Task<ConflictDecision>>? conflict;
    private readonly Dictionary<string, string> rootMappings = new(StringComparer.Ordinal);
    private readonly List<ExtractionFailure> failures = [];
    private readonly HashSet<long> processed = [];
    private FileStream? stream;
    private DirectoryGuard? guard;
    private DirectoryGuard? destinationGuard;
    private string? temporary;
    private string? target;
    private ArchiveEntry? active;
    private bool replace;
    private bool ignored;
    private bool alreadyFailed;
    private ConflictChoice? policy;
    private ulong currentBytes;
    private readonly string? zoneIdentifier;
    public ulong Bytes { get; private set; }
    public int Completed { get; private set; }
    public int Skipped { get; private set; }
    public string Destination { get; private set; }
    public string? CurrentPath => active?.Path;
    public uint[] EntryIds => entries.Keys.Order().Select(id => checked((uint)id)).ToArray();
    public bool IsComplete => active is null && processed.Count == entries.Count;

    public ExtractionWriter(string archivePath, IReadOnlyList<ArchiveEntry> source, ExtractionOptions options,
        Func<string, CancellationToken, Task<ConflictDecision>>? conflict)
    {
        this.options = options;
        this.conflict = conflict;
        zoneIdentifier = ReadZone(archivePath);
        policy = options.ConflictPolicy;
        entries = source.Where(e => options.SelectedIds is null || options.SelectedIds.Contains(e.Id)).ToDictionary(e => e.Id);
        Destination = Path.GetFullPath(options.Destination);
        if (options.SelectedIds?.Length > 100000) throw new ArchiveOperationException("ResourceLimit");
        // Validate the entire selection before creating any output.
        var directoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            string relative = ArchivePathPolicy.Validate(entry.Path);
            if (entry.IsLink) throw new ArchiveOperationException("LinksNotSupported");
            for (string? directory = entry.IsDirectory ? relative : Path.GetDirectoryName(relative);
                !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                if (directoryNames.TryGetValue(directory, out string? original) && original != directory)
                    throw new ArchiveOperationException("DirectoryCaseConflict");
                directoryNames[directory] = directory;
            }
        }
        if (entries.Count == 0) return;
        var total = entries.Values.Aggregate(0UL, (sum, e) => checked(sum + (e.Size ?? 0)));
        if (total > options.ByteLimit) throw new ArchiveOperationException("ResourceLimit");
        destinationGuard = DirectoryGuard.Acquire(Destination, create: true);
        try
        {
            if (options.Smart)
            {
                var plan = SmartExtractionPlan.Create(entries.Values);
                if (plan.Kind == SmartExtractionKind.Container)
                {
                    string name = ArchivePathPolicy.Validate(Path.GetFileNameWithoutExtension(archivePath));
                    Destination = ReserveDirectory(Destination, name);
                }
                else if (plan.Kind == SmartExtractionKind.SingleDirectory)
                {
                    string original = ArchivePathPolicy.Validate(plan.RootName!);
                    string reserved = ReserveDirectory(Destination, original);
                    rootMappings[original] = Path.GetFileName(reserved);
                }
                policy = ConflictChoice.Rename;
            }
        }
        catch { destinationGuard.Dispose(); destinationGuard = null; throw; }
    }

    private static string ReserveDirectory(string parent, string name)
    {
        for (int suffix = 1; suffix < 10000; suffix++)
        {
            string path = Path.Combine(parent, suffix == 1 ? name : $"{name} ({suffix})");
            if (DirectoryGuard.CreateDirectory(path, IntPtr.Zero)) return path;
            if (Marshal.GetLastWin32Error() != 183) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        throw new ArchiveOperationException("DestinationConflict");
    }

    public async Task BeginAsync(long id, CancellationToken token)
    {
        if (active is not null || !processed.Add(id) || !entries.TryGetValue(id, out var entry))
            throw new ArchiveOperationException("ProtocolMismatch");
        active = entry;
        ignored = false;
        alreadyFailed = false;
        replace = false;
        currentBytes = 0;
        string relative = ArchivePathPolicy.Validate(entry.Path);
        string first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (rootMappings.TryGetValue(first, out var mapped)) relative = mapped + relative[first.Length..];
        target = Path.GetFullPath(Path.Combine(Destination, relative));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(Destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveOperationException("UnsafePath");
        if (entry.IsDirectory)
        {
            guard = DirectoryGuard.Acquire(target, create: true);
            return;
        }
        guard = DirectoryGuard.Acquire(Path.GetDirectoryName(target)!, create: true);
        if (Exists(target))
        {
            var decision = policy is { } fixedChoice ? new ConflictDecision(fixedChoice) : conflict is not null
                ? await conflict(target, token) : new ConflictDecision(ConflictChoice.Skip);
            if (decision.ApplyToAll) policy = decision.Choice;
            switch (decision.Choice)
            {
                case ConflictChoice.Cancel: throw new OperationCanceledException(token);
                case ConflictChoice.Skip: ignored = true; break;
                case ConflictChoice.Rename: target = FindFreeName(target); break;
                case ConflictChoice.Replace:
                    if (Directory.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new ArchiveOperationException("UnsafePath");
                    replace = true;
                    break;
            }
        }
        if (ignored) return;
        temporary = Path.Combine(Path.GetDirectoryName(target)!, $".ailearc-{Guid.NewGuid():N}.partial");
        stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
    }

    public async Task WriteAsync(long id, byte[] bytes, CancellationToken token)
    {
        if (active?.Id != id || bytes.Length > 32 * 1024 || active.IsDirectory) throw new ArchiveOperationException("ProtocolMismatch");
        Bytes = checked(Bytes + (uint)bytes.Length);
        currentBytes += (uint)bytes.Length;
        if (Bytes > options.ByteLimit || (active.Size is { } size && currentBytes > size)) throw new ArchiveOperationException("ResourceLimit");
        if (stream is not null) await stream.WriteAsync(bytes, token);
    }

    public async Task CompleteAsync(long id, int nativeResult, CancellationToken token)
    {
        if (active?.Id != id) throw new ArchiveOperationException("ProtocolMismatch");
        try
        {
            if (ignored) { Skipped++; return; }
            if (nativeResult != 0 || (!active.IsDirectory && active.Size is { } size && currentBytes != size))
            {
                failures.Add(new(active.Path, nativeResult == 9 ? "WrongPasswordOrDamaged" : "IntegrityFailed"));
                return;
            }
            if (stream is not null)
            {
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
                await stream.DisposeAsync();
                stream = null;
                if (zoneIdentifier is not null)
                    await File.WriteAllTextAsync(temporary + ":Zone.Identifier", zoneIdentifier, System.Text.Encoding.ASCII, token);
                token.ThrowIfCancellationRequested();
                if (!replace)
                {
                    while (true)
                    {
                        try { File.Move(temporary!, target!, overwrite: false); break; }
                        catch (IOException) when (policy == ConflictChoice.Rename && Exists(target!)) { target = FindFreeName(target!); }
                    }
                }
                else File.Move(temporary!, target!, overwrite: true);
                temporary = null;
            }
            Completed++;
        }
        finally { await ClearActiveAsync(); }
    }

    public ExtractionResult Result(bool cancelled = false, string? errorCode = null)
    {
        if (errorCode is not null && !alreadyFailed)
        {
            failures.Add(new(active?.Path ?? "", errorCode));
            alreadyFailed = true;
        }
        string output = rootMappings.Count == 1 ? Path.Combine(Destination, rootMappings.Values.Single()) : Destination;
        return new(output, Completed, Skipped, failures.ToArray(), cancelled);
    }
    private static string? ReadZone(string archivePath)
    {
        try
        {
            using var zone = new FileStream(archivePath + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read);
            if (zone.Length > 16384) throw new ArchiveOperationException("InvalidSourceZone");
            using var reader = new StreamReader(zone);
            foreach (string line in reader.ReadToEnd().Split('\n'))
                if (line.Trim() is "ZoneId=3" or "ZoneId=4") return "[ZoneTransfer]\r\n" + line.Trim() + "\r\n";
            return null;
        }
        catch (FileNotFoundException) { return null; }
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static string FindFreeName(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        string extension = Path.GetExtension(target);
        string stem = Path.GetFileNameWithoutExtension(target);
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!Exists(candidate)) return candidate;
        }
        throw new ArchiveOperationException("DestinationConflict");
    }
    private async Task ClearActiveAsync()
    {
        try
        {
            if (stream is not null) { await stream.DisposeAsync(); stream = null; }
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
        finally { temporary = null; active = null; guard?.Dispose(); guard = null; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await ClearActiveAsync(); }
        finally { destinationGuard?.Dispose(); destinationGuard = null; }
    }
}
