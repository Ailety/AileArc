namespace AileArc.Shared;

public sealed record CompressionItem(string SourcePath, string EntryPath, bool IsDirectory, long Length, long LastWriteTime);
public sealed record CompressionBatch(CompressionItem[] Items);
