using AileArc.Shared;

namespace AileArc.Core;

public sealed record BrowserEntry(long Id, string Name, string Path, string Parent, bool IsDirectory, ulong? Size, bool Encrypted)
{
    public string Kind { get; } = IsDirectory ? "folder" : System.IO.Path.GetExtension(Name).ToLowerInvariant() switch { ".pdf" => "pdf", ".md" => "md", ".txt" => "text", ".json" or ".yml" or ".cs" or ".xml" => "code", ".png" or ".jpg" or ".svg" => "image", _ => "file" };
}
public enum EntrySort { Name, Size, Type, Path }

/// <summary>Pure archive paths, never destination filesystem paths. Case and duplicate files are retained.</summary>
public sealed class ArchiveIndex
{
    private readonly BrowserEntry[] entries;
    private readonly HashSet<string> directories = new(StringComparer.Ordinal);
    public bool ContainsDirectory(string path) => directories.Contains(path);
    private readonly Dictionary<string, BrowserEntry[]> children;
    public IReadOnlyList<BrowserEntry> Entries => entries;

    public ArchiveIndex(IEnumerable<ArchiveEntry> source, CancellationToken cancellationToken = default)
    {
        var rows = source.TryGetNonEnumeratedCount(out int count) ? new List<BrowserEntry>(count) : new List<BrowserEntry>();
        var missing = new Stack<string>();
        long syntheticId = -1;
        string EnsureParent(string parent)
        {
            if (parent.Length == 0) return parent;
            if (directories.TryGetValue(parent, out var existing)) return existing;
            for (string current = parent; current.Length > 0 && !directories.Contains(current); current = ParentOf(current))
                missing.Push(current);
            while (missing.TryPop(out var directory))
            {
                directories.Add(directory);
                rows.Add(new BrowserEntry(syntheticId--, NameOf(directory), directory, ParentOf(directory), true, null, false));
            }
            return parent;
        }
        foreach (var entry in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Path.Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0) continue;
            var parent = EnsureParent(ParentOf(path));
            // Explicit folders already synthesized are the same navigation location; raw entries remain in ArchiveScan.
            if (entry.IsDirectory && !directories.Add(path)) continue;
            rows.Add(new BrowserEntry(entry.Id, NameOf(path), path, parent, entry.IsDirectory, entry.Size, entry.Encrypted));
        }
        entries = rows.ToArray();
        children = directories.Count == 0 ? new(StringComparer.Ordinal) { [""] = entries } :
            entries.GroupBy(e => e.Parent, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    public BrowserEntry[] Query(string directory, string search = "", bool currentDirectoryOnly = false,
        EntrySort sort = EntrySort.Name, bool descending = false, CancellationToken cancellationToken = default, Func<string,string>? typeLabel = null)
    {
        var candidates = search.Length == 0 || currentDirectoryOnly
            ? children.GetValueOrDefault(directory, []) : entries;
        cancellationToken.ThrowIfCancellationRequested();
        BrowserEntry[] result;
        if (search.Length == 0) result = (BrowserEntry[])candidates.Clone();
        else
        {
            var matches = new List<BrowserEntry>();
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) matches.Add(entry);
            }
            result = matches.ToArray();
        }
        int Compare(BrowserEntry left, BrowserEntry right)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int folder = right.IsDirectory.CompareTo(left.IsDirectory);
            if (folder != 0) return folder;
            int comparison = sort switch
            {
                EntrySort.Size => Nullable.Compare(left.Size, right.Size),
                EntrySort.Path => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path),
                EntrySort.Type => StringComparer.OrdinalIgnoreCase.Compare(typeLabel?.Invoke(left.Kind) ?? left.Kind, typeLabel?.Invoke(right.Kind) ?? right.Kind),
                _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
            };
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            if (comparison == 0) comparison = left.Id.CompareTo(right.Id);
            return descending ? -comparison : comparison;
        }
        for (int i = 1; i < result.Length; i++)
        {
            if (Compare(result[i - 1], result[i]) <= 0) continue;
            Array.Sort(result, Compare);
            break;
        }
        return result;
    }

    public static string ParentOf(string path) => path.LastIndexOf('/') is int i && i >= 0 ? path[..i] : "";
    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
