using AileArc.Core.Extraction;
using AileArc.Shared;

namespace AileArc.Core.Compression;

public sealed record CompressionOptions(string[] Sources, string Destination, string Format = "ZIP", string Preset = "Balanced",
    string? Password = null, bool EncryptNames = false, bool Overwrite = false, ulong ByteLimit = 4UL * 1024 * 1024 * 1024);
public sealed record CompressionProgress(string Phase, ulong Completed, ulong Total);
public sealed record CompressionResult(string Destination, int Entries, ulong SourceBytes);

internal sealed class CompressionPlan : IDisposable
{
    private readonly List<DirectoryGuard> guards = [];
    private readonly HashSet<string> guarded = new(StringComparer.OrdinalIgnoreCase);
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
        try
        {
            var sources = options.Sources.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            sources = sources.Where(p => !sources.Any(other => other != p && p.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long characters = 0;
            foreach (string source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveOperationException("DestinationInsideSource");
                string rootName = ArchivePathPolicy.Validate(Path.GetFileName(source));
                if (!names.Add(rootName)) throw new ArchiveOperationException("CompressionNameConflict");
                var pending = new Stack<(string Source, string Entry)>();
                pending.Push((source, rootName));
                while (pending.TryPop(out var candidate))
                {
                    token.ThrowIfCancellationRequested();
                    string entryName = ArchivePathPolicy.Validate(candidate.Entry);
                    var attributes = File.GetAttributes(candidate.Source);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("LinksNotSupported");
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    plan.Pin(directory ? candidate.Source : Path.GetDirectoryName(candidate.Source)!);
                    long size = directory ? 0 : new FileInfo(candidate.Source).Length;
                    plan.Total = checked(plan.Total + (ulong)size);
                    characters += candidate.Source.Length + entryName.Length;
                    if (plan.Items.Count >= 10000 || plan.Total > options.ByteLimit || characters > 4 * 1024 * 1024)
                        throw new ArchiveOperationException("ResourceLimit");
                    plan.Items.Add(new(candidate.Source, entryName, directory, size, File.GetLastWriteTimeUtc(candidate.Source).ToFileTimeUtc()));
                    if (directory)
                        foreach (string child in Directory.EnumerateFileSystemEntries(candidate.Source).Order(StringComparer.Ordinal))
                            pending.Push((child, Path.Combine(entryName, Path.GetFileName(child))));
                }
            }
            return plan;
        }
        catch { plan.Dispose(); throw; }
    }
    private void Pin(string path) { if (guarded.Add(path)) guards.Add(DirectoryGuard.Acquire(path, create: false)); }
    public void Dispose() { foreach (var guard in guards) guard.Dispose(); }
}
