using AileArc.Shared;

namespace AileArc.Core;

public sealed record BrowserEntry(long Id, string Name, string Path, string Parent, bool IsDirectory, ulong? Size, bool Encrypted);
public enum EntrySort { Name, Size }

/// <summary>Pure archive paths, never destination filesystem paths. Case and duplicate files are retained.</summary>
public sealed class ArchiveIndex
{
    private readonly BrowserEntry[] entries;
    private readonly Dictionary<string, BrowserEntry[]> children;
    public IReadOnlyList<BrowserEntry> Entries => entries;

    public ArchiveIndex(IEnumerable<ArchiveEntry> source, CancellationToken cancellationToken = default)
    {
        var rows = new List<BrowserEntry>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        long syntheticId = -1;
        foreach (var entry in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Path.Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0) continue;
            var parent = ParentOf(path);
            var missing = new Stack<string>();
            for (string current = parent; current.Length > 0 && !directories.Contains(current); current = ParentOf(current))
                missing.Push(current);
            while (missing.TryPop(out var directory))
            {
                directories.Add(directory);
                rows.Add(new BrowserEntry(syntheticId--, NameOf(directory), directory, ParentOf(directory), true, null, false));
            }
            // Explicit folders already synthesized are the same navigation location; raw entries remain in ArchiveScan.
            if (entry.IsDirectory && !directories.Add(path)) continue;
            rows.Add(new BrowserEntry(entry.Id, NameOf(path), path, parent, entry.IsDirectory, entry.Size, entry.Encrypted));
        }
        entries = rows.ToArray();
        children = entries.GroupBy(e => e.Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    public BrowserEntry[] Query(string directory, string search = "", bool currentDirectoryOnly = false,
        EntrySort sort = EntrySort.Name, bool descending = false, CancellationToken cancellationToken = default)
    {
        var candidates = search.Length == 0 || currentDirectoryOnly
            ? children.GetValueOrDefault(directory, []) : entries;
        var result = new List<BrowserEntry>();
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) result.Add(entry);
        }
        result.Sort((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int folder = right.IsDirectory.CompareTo(left.IsDirectory);
            if (folder != 0) return folder;
            int comparison = sort == EntrySort.Size ? Nullable.Compare(left.Size, right.Size)
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            if (comparison == 0) comparison = left.Id.CompareTo(right.Id);
            return descending ? -comparison : comparison;
        });
        return result.ToArray();
    }

    public static string ParentOf(string path) => path.LastIndexOf('/') is int i && i >= 0 ? path[..i] : "";
    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
