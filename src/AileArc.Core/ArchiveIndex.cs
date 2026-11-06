using AileArc.Shared;

namespace AileArc.Core;

public sealed record BrowserEntry(long Id, string Name, string Path, string Parent, bool IsDirectory, ulong? Size, bool Encrypted, long? ModifiedFileTime = null)
{
    // Classify the displayed filename once during indexing. Format detection and launch policy use their own checks.
    public string Kind { get; } = IsDirectory ? "folder" : System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".pdf" => "pdf",
        ".md" or ".markdown" => "md",
        ".txt" or ".log" => "text",
        ".json" or ".yaml" or ".yml" or ".xml" or ".toml" or ".ini" or ".cfg" or ".conf" or
        ".cs" or ".c" or ".h" or ".cpp" or ".hpp" or ".py" or ".rs" or ".go" or ".java" or
        ".js" or ".jsx" or ".ts" or ".tsx" or ".css" or ".html" or ".sql" or ".ps1" or ".sh" => "code",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp" or ".ico" or ".tif" or ".tiff" or ".avif" or ".heic" => "image",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".zst" => "archive",
        ".doc" or ".docx" or ".odt" or ".rtf" => "document",
        ".xls" or ".xlsx" or ".ods" or ".csv" or ".tsv" => "spreadsheet",
        ".ppt" or ".pptx" or ".odp" => "presentation",
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" => "audio",
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" => "video",
        ".exe" or ".com" or ".msi" or ".msp" or ".scr" => "application",
        _ => "file"
    };
}
public enum EntrySort { Name, Size, Type, Path, Modified }

/// <summary>Pure archive paths, never destination filesystem paths. Case and duplicate files are retained.</summary>
public sealed class ArchiveIndex
{
    private readonly BrowserEntry[] entries;
    private readonly Dictionary<string, int> directories = new(StringComparer.Ordinal);
    public bool ContainsDirectory(string path) => directories.ContainsKey(path);
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
            if (directories.TryGetValue(parent, out int existing)) return rows[existing].Path;
            for (string current = parent; current.Length > 0 && !directories.ContainsKey(current); current = ParentOf(current))
                missing.Push(current);
            while (missing.TryPop(out var directory))
            {
                directories.Add(directory, rows.Count);
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
            if (entry.IsDirectory && !directories.TryAdd(path, rows.Count))
            {
                int index = directories[path];
                if (rows[index].ModifiedFileTime is null && entry.ModifiedFileTime.HasValue)
                    rows[index] = rows[index] with { ModifiedFileTime = entry.ModifiedFileTime };
                continue;
            }
            rows.Add(new BrowserEntry(entry.Id, NameOf(path), path, parent, entry.IsDirectory, entry.Size, entry.Encrypted, entry.ModifiedFileTime));
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
            if (sort == EntrySort.Modified && left.ModifiedFileTime.HasValue != right.ModifiedFileTime.HasValue)
                return left.ModifiedFileTime.HasValue ? -1 : 1;
            int comparison = sort switch
            {
                EntrySort.Size => Nullable.Compare(left.Size, right.Size),
                EntrySort.Modified => Nullable.Compare(left.ModifiedFileTime, right.ModifiedFileTime),
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
