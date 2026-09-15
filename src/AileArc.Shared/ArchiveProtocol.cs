using System.Buffers.Binary;
using System.Text.Json;

namespace AileArc.Shared;

/// <summary>A session-local ID is independent of the entry's potentially duplicate path.</summary>
public sealed record ArchiveEntry(long Id, string Path, bool IsDirectory, ulong? Size, bool Encrypted, bool IsLink = false);
public sealed record ArchiveIdentity(string FileId, long Length, long LastWriteTime);
public sealed record ScanRequest(int Version, string Path, string Operation = "scan", string? Password = null, ulong ByteLimit = 4UL * 1024 * 1024 * 1024, int NameCodePage = 0,
    string? CompressionFormat = null, string? CompressionPreset = null, int CompressionCount = 0, bool EncryptNames = false);
public sealed record ExtractionCommand(uint[]? EntryIds = null);
public sealed record ScanMessage(string Kind, string? Format = null, ArchiveEntry[]? Entries = null, string? ErrorCode = null,
    long? EntryId = null, byte[]? Data = null, int? Result = null, ArchiveIdentity? Identity = null, ulong? CompletedBytes = null, ulong? TotalBytes = null);

/// <summary>Length-prefixed JSON over the child process's private redirected pipes.</summary>
public static class ArchiveProtocol
{
    public const int Version = 5;
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxEntries = 250_000;
    public const long MaxMetadataCharacters = 32 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Frame limit exceeded.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid frame size.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty frame.");
    }
}
