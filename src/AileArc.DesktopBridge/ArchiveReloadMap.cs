using AileArc.Core;

namespace AileArc.DesktopBridge;

public sealed class ArchiveReloadMap
{
    public IReadOnlyDictionary<string, string> Directories { get; }
    private readonly Dictionary<long, long?> folderIds;
    private ArchiveReloadMap(Dictionary<string, string> directories, Dictionary<long, long?> folderIds)
    { Directories = directories; this.folderIds = folderIds; }

    public long? TranslateId(long id) => folderIds.TryGetValue(id, out var translated) ? translated : id;

    public static ArchiveReloadMap? Create(ArchivePresentation before, ArchivePresentation after, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (before.Scan.Identity is null || before.Scan.Identity != after.Scan.Identity || before.Scan.Format != after.Scan.Format ||
            before.Scan.Entries.Count != after.Scan.Entries.Count) return null;
        var forward = new Dictionary<string, string?>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string?>(StringComparer.Ordinal);
        var visited = new HashSet<(string, string)>();
        for (int i = 0; i < before.Scan.Entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var original = before.Scan.Entries[i]; var current = after.Scan.Entries[i];
            if (original.Id != current.Id || original.IsDirectory != current.IsDirectory || original.Size != current.Size || original.ModifiedFileTime != current.ModifiedFileTime ||
                original.Encrypted != current.Encrypted || original.IsLink != current.IsLink ||
                (before.Scan.NameCodePage == after.Scan.NameCodePage && original.Path != current.Path)) return null;
            string left = original.Path.Replace('\\', '/').TrimEnd('/');
            string right = current.Path.Replace('\\', '/').TrimEnd('/');
            if (!original.IsDirectory) { left = ArchiveIndex.ParentOf(left); right = ArchiveIndex.ParentOf(right); }
            if (!visited.Add((left, right))) continue;
            // Compare at equal depths from the root. A separator can itself be
            // reinterpreted by a multibyte encoding. Missing depths are ambiguous.
            var oldParents = Ancestors(left); var newParents = Ancestors(right);
            while (oldParents.Count != 0 || newParents.Count != 0)
            {
                oldParents.TryPop(out var oldPath); newParents.TryPop(out var newPath);
                Record(forward, oldPath, newPath); Record(reverse, newPath, oldPath);
            }
        }
        var paths = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = "" };
        foreach (var (oldPath, newPath) in forward)
        {
            token.ThrowIfCancellationRequested();
            if (newPath is not null && reverse[newPath] == oldPath) paths.Add(oldPath, newPath);
        }
        var newIds = after.Index.Entries.Where(row => row.IsDirectory).ToDictionary(row => row.Path, row => row.Id, StringComparer.Ordinal);
        var ids = new Dictionary<long, long?>();
        foreach (var row in before.Index.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (row.IsDirectory) ids.Add(row.Id, paths.TryGetValue(row.Path, out var path) ? newIds[path] : null);
        }
        return new(paths, ids);
    }

    private static Stack<string> Ancestors(string path)
    {
        var result = new Stack<string>();
        while (path.Length != 0) { result.Push(path); path = ArchiveIndex.ParentOf(path); }
        return result;
    }

    private static void Record(Dictionary<string, string?> table, string? path, string? counterpart)
    {
        if (path is not null && !table.TryAdd(path, counterpart) && table[path] != counterpart) table[path] = null;
    }
}
