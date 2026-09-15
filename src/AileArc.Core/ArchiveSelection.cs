using AileArc.Shared;

namespace AileArc.Core;

public static class ArchiveSelection
{
    /// <summary>Directory selections include descendants; file selections use IDs, never ambiguous paths.</summary>
    public static long[] Expand(IEnumerable<BrowserEntry> selected, IReadOnlyList<ArchiveEntry> entries)
    {
        var selection = selected.ToArray();
        var files = selection.Where(e => !e.IsDirectory).Select(e => e.Id).ToHashSet();
        string[] directories = selection.Where(e => e.IsDirectory).Select(e => e.Path.TrimEnd('/')).Distinct(StringComparer.Ordinal).ToArray();
        return entries.Where(entry =>
        {
            if (files.Contains(entry.Id)) return true;
            string path = entry.Path.Replace('\\', '/').TrimEnd('/');
            return directories.Any(directory => (entry.IsDirectory && path == directory) || path.StartsWith(directory + "/", StringComparison.Ordinal));
        }).Select(e => e.Id).Distinct().Order().ToArray();
    }
}
