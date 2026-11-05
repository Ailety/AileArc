using AileArc.Shared;

namespace AileArc.Core;

public static class ArchiveSelection
{
    /// <summary>Directory selections include descendants; file selections use IDs, never ambiguous paths.</summary>
    public static long[] Expand(IEnumerable<BrowserEntry> selected, IReadOnlyList<ArchiveEntry> entries, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var files = new HashSet<long>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            if (row.IsDirectory) directories.Add(row.Path.TrimEnd('/'));
            else files.Add(row.Id);
        }
        var matches = new HashSet<long>();
        var folders = directories.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (files.Contains(entry.Id)) { matches.Add(entry.Id); continue; }
            if (directories.Count == 0) continue;
            var path = entry.Path.Replace('\\', '/').AsSpan().TrimEnd('/');
            if (entry.IsDirectory && folders.Contains(path)) { matches.Add(entry.Id); continue; }
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] != '/' || !folders.Contains(path[..i])) continue;
                matches.Add(entry.Id);
                break;
            }
        }
        return matches.Order().ToArray();
    }
}
