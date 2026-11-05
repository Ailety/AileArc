using AileArc.Core.Extraction;
using AileArc.Core.Storage;
using System.ComponentModel;
using AileArc.Shared;

namespace AileArc.Core.Compression;

public sealed record CompressionOptions(string[] Sources, string Destination, string Format = "ZIP", string Preset = "Balanced",
    string? Password = null, bool EncryptNames = false, bool Overwrite = false, ulong ByteLimit = 4UL * 1024 * 1024 * 1024,
    CompressionDestination? ConfirmedDestination = null);
public sealed record CompressionProgress(string Phase, ulong Completed, ulong Total);
public sealed record CompressionResult(string Destination, int Entries, ulong SourceBytes);

internal sealed class CompressionPlan : IDisposable
{
    private readonly Dictionary<string, DirectoryGuard> guards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> commitHandles = [];
    public List<CompressionItem> Items { get; } = [];
    public ulong Total { get; private set; }

    public static CompressionPlan Build(CompressionOptions options, CancellationToken token)
    {
        if (options.Format is not ("ZIP" or "7Z") || options.Preset is not ("Fast" or "Balanced" or "Maximum") ||
            options.Sources.Length == 0 || options.ByteLimit > 4UL * 1024 * 1024 * 1024 || (options.EncryptNames && (options.Format != "7Z" || string.IsNullOrEmpty(options.Password))))
            throw new ArchiveOperationException("CompressionOptionsInvalid");
        string destination = Path.GetFullPath(options.Destination);
        _ = ArchivePathPolicy.Validate(Path.GetFileName(destination));
        if (!Path.GetExtension(destination).Equals(options.Format == "ZIP" ? ".zip" : ".7z", StringComparison.OrdinalIgnoreCase))
            throw new ArchiveOperationException("CompressionExtensionMismatch");
        var plan = new CompressionPlan();
        string? examining = null;
        try
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>();
            foreach (string path in options.Sources)
            {
                token.ThrowIfCancellationRequested();
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (selected.Add(full)) sources.Add(full);
            }
            sources.RemoveAll(source =>
            {
                token.ThrowIfCancellationRequested();
                string? parent = Path.GetDirectoryName(source);
                while (parent is not null && !selected.Contains(parent)) parent = Path.GetDirectoryName(parent);
                return parent is not null;
            });
            if (sources.Count > 10_000) throw new ArchiveOperationException("CompressionManifestLimit");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int discovered = 0;
            long characters = 0;
            void Reserve(string sourcePath, string entryPath)
            {
                characters += sourcePath.Length + entryPath.Length;
                if (++discovered > 10_000 || characters > 4 * 1024 * 1024)
                    throw new ArchiveOperationException("CompressionManifestLimit");
            }
            foreach (string source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveOperationException("DestinationInsideSource");
                string rootName = ArchivePathPolicy.Validate(Path.GetFileName(source));
                if (!names.Add(rootName)) throw new ArchiveOperationException("CompressionNameConflict");
                var pending = new Stack<(string Source, string Entry)>();
                Reserve(source, rootName);
                pending.Push((source, rootName));
                while (pending.TryPop(out var candidate))
                {
                    token.ThrowIfCancellationRequested();
                    examining = candidate.Source;
                    string entryName = ArchivePathPolicy.Validate(candidate.Entry);
                    var attributes = File.GetAttributes(candidate.Source);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("LinksNotSupported");
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    var guard = plan.Pin(directory ? candidate.Source : Path.GetDirectoryName(candidate.Source)!);
                    ArchiveIdentity identity;
                    if (directory) identity = guard.Snapshot();
                    else
                    {
                        using var file = VerifiedFile.OpenRegularInput(candidate.Source, FileShare.Read);
                        identity = ArchiveFileIdentity.Read(file);
                        plan.Total = checked(plan.Total + (ulong)identity.Length);
                    }
                    if (plan.Total > options.ByteLimit) throw new ArchiveOperationException("CompressionByteLimit");
                    plan.Items.Add(new(candidate.Source, entryName, directory, identity));
                    if (!directory) continue;
                    var children = new List<(string Source, string Entry)>();
                    foreach (string child in Directory.EnumerateFileSystemEntries(candidate.Source))
                    {
                        token.ThrowIfCancellationRequested();
                        string childName = Path.Combine(entryName, Path.GetFileName(child));
                        Reserve(child, childName);
                        children.Add((child, childName));
                    }
                    children.Sort((a, b) => StringComparer.Ordinal.Compare(a.Source, b.Source));
                    foreach (var child in children) pending.Push(child);
                }
            }
            return plan;
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        { plan.Dispose(); throw new ArchiveOperationException("SourceChanged", examining); }
        catch { plan.Dispose(); throw; }
    }
    private DirectoryGuard Pin(string path)
    {
        if (!guards.TryGetValue(path, out var guard)) guards.Add(path, guard = DirectoryGuard.Acquire(path, create: false));
        return guard;
    }
    internal void ValidateForCommit(CancellationToken token)
    {
        foreach (var item in Items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ArchiveIdentity actual;
                if (item.IsDirectory) actual = guards[item.SourcePath].Snapshot();
                else
                {
                    var file = VerifiedFile.OpenRegularInput(item.SourcePath, FileShare.Read);
                    commitHandles.Add(file);
                    actual = ArchiveFileIdentity.Read(file);
                }
                if (actual != item.Snapshot) throw new ArchiveOperationException("SourceChanged", item.SourcePath);
            }
            catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or ArchiveOperationException { Code: "UnsafePath" })
            { throw new ArchiveOperationException("SourceChanged", item.SourcePath); }
        }
    }
    public void Dispose()
    {
        foreach (var file in commitHandles) file.Dispose();
        foreach (var guard in guards.Values) guard.Dispose();
    }
}
