using AileArc.Shared;

namespace AileArc.Core;

public enum SmartExtractionKind { Empty, SingleDirectory, SingleFile, Container }
public sealed record SmartExtractionPlan(SmartExtractionKind Kind, string? RootName)
{
    /// <summary>Plans structure only. Never authorizes writing or replaces destination path validation.</summary>
    public static SmartExtractionPlan Create(IEnumerable<ArchiveEntry> source)
    {
        var entries = source.ToArray();
        var index = new ArchiveIndex(entries);
        var roots = index.Query("");
        if (roots.Length == 0) return new(SmartExtractionKind.Empty, null);
        if (roots.Length == 1)
        {
            var root = roots[0];
            return new(root.IsDirectory ? SmartExtractionKind.SingleDirectory : SmartExtractionKind.SingleFile, root.Name);
        }
        return new(SmartExtractionKind.Container, null);
    }
}
