using System.ComponentModel;
using System.Windows;
using AileArc.Core;

namespace AileArc.Client;

// The hierarchy contains only archive directories. The ListBox virtualizes its visible rows;
// expanding a branch does not instantiate controls for all of its descendants.
internal sealed class ArchiveFolderTree
{
    private readonly Dictionary<string, FolderNode> nodes = new(StringComparer.Ordinal);
    internal FolderNode Root { get; }
    internal ArchiveFolderTree(string archiveName, ArchiveIndex index, CancellationToken token)
    {
        Root = new(archiveName, "", null);
        nodes.Add("", Root);
        foreach (var entry in index.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.IsDirectory) continue;
            // ArchiveIndex emits even implicit parents before their children.
            var parent = nodes[entry.Parent];
            var node = new FolderNode(entry.Name, entry.Path, parent, entry.Id);
            parent.Add(node); nodes.Add(entry.Path, node);
        }
        foreach (var node in nodes.Values) { token.ThrowIfCancellationRequested(); node.SortChildren(); }
        Root.Expanded = Root.HasChildren;
    }
    internal FolderNode this[string path] => nodes[path];
    internal void RestoreExpansion(ArchiveFolderTree previous, IReadOnlyDictionary<string, string> paths, CancellationToken token)
    {
        foreach (var node in previous.nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (paths.TryGetValue(node.Path, out var path)) nodes[path].Expanded = node.Expanded;
        }
    }
    internal List<FolderNode> VisibleRows()
    {
        var rows = new List<FolderNode>(); var pending = new Stack<FolderNode>();
        pending.Push(Root);
        while (pending.TryPop(out var node))
        {
            rows.Add(node);
            if (node.Expanded)
                for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
        return rows;
    }
}

internal sealed class FolderNode(string name, string path, FolderNode? parent, long? entryId = null) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    internal long? EntryId { get; } = entryId;
    public string Path { get; } = path;
    public string Hint => Parent is null ? Name : Path;
    public string Glyph => Parent is null ? "\uE7B8" : "\uE8B7";
    internal FolderNode? Parent { get; } = parent;
    internal int Depth { get; } = (parent?.Depth ?? -1) + 1;
    public Thickness Indent => new(Math.Min(Depth, 8) * 11, 0, 0, 0);
    private List<FolderNode>? children;
    internal IReadOnlyList<FolderNode> Children => children is null ? Array.Empty<FolderNode>() : children;
    public bool HasChildren => children is { Count: > 0 };
    internal void Add(FolderNode node) => (children ??= []).Add(node);
    internal void SortChildren() => children?.Sort((a,b) =>
    {
        int n = StringComparer.OrdinalIgnoreCase.Compare(a.Name,b.Name);
        return n != 0 ? n : StringComparer.Ordinal.Compare(a.Name,b.Name);
    });
    private bool expanded;
    internal bool Expanded
    {
        get => expanded;
        set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new(nameof(Arrow))); PropertyChanged?.Invoke(this, new(nameof(ToggleHint))); }
    }
    public string Arrow => Expanded ? "▾" : "▸";
    public string ToggleHint => ClientApp.Text.Format(Expanded ? "ClientCollapseFolder" : "ClientExpandFolder", Name);
    public event PropertyChangedEventHandler? PropertyChanged;
}
