namespace AileArc.Shared;

public sealed record CompressionItem(string SourcePath, string EntryPath, bool IsDirectory, ArchiveIdentity Snapshot)
{
    [System.Text.Json.Serialization.JsonIgnore] public long Length => IsDirectory ? 0 : Snapshot.Length;
    [System.Text.Json.Serialization.JsonIgnore] public long LastWriteTime => Snapshot.LastWriteTime;
}
public sealed record CompressionBatch(CompressionItem[] Items);
