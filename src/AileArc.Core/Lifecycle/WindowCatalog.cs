namespace AileArc.Core.Lifecycle;

/// <summary>UI-thread-owned registry. File IDs deduplicate hard-link aliases; normalized paths deduplicate repeat launches.</summary>
public sealed class WindowCatalog<T> where T : class
{
    private sealed record Entry(T Window, string Path, string? FileId);
    private readonly List<Entry> entries = [];
    public T? Find(string path, string? fileId)
    {
        string canonical = Path.GetFullPath(path);
        return entries.LastOrDefault(e => string.Equals(e.Path, canonical, StringComparison.OrdinalIgnoreCase) ||
            (fileId is not null && e.FileId == fileId))?.Window;
    }
    public void Register(T window, string path, string? fileId) => entries.Add(new(window, Path.GetFullPath(path), fileId));
    public void Remove(T window) => entries.RemoveAll(e => ReferenceEquals(e.Window, window));
}
