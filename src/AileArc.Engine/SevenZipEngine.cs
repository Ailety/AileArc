using System.Runtime.InteropServices;
using AileArc.Shared;

namespace AileArc.Engine;

public sealed class ArchiveEngineException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Native read/decode adapter. Must only be instantiated inside an isolated Worker.</summary>
public sealed partial class SevenZipEngine : IDisposable
{
    private readonly IntPtr library;
    private readonly CreateObjectDelegate createObject;
    private sealed record ArchiveFormat(string Name, byte Id, byte[][] Signatures);
    private const int MaxArchivePrefix = 4 * 1024 * 1024;
    private static readonly ArchiveFormat[] Formats =
    [
        new("ZIP", 0x01, [[0x50, 0x4b, 3, 4], [0x50, 0x4b, 5, 6], [0x50, 0x4b, 6, 6], [0x50, 0x4b, 7, 8]]),
        new("7Z", 0x07, [[0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c]]),
        new("RAR", 0x03, [[0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00]]),
        new("RAR5", 0xCC, [[0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00]])
    ];

    private static IEnumerable<(string Name, byte Id, ulong Search)> ProbeOrder(FileStream input)
    {
        foreach (var format in Formats) yield return (format.Name, format.Id, 0);
        // A recognizable but broken outer archive must not reveal a nested archive
        // instead. With a preamble, try the earliest candidate handler first.
        input.Position = 0;
        var prefix = new byte[(int)Math.Min(input.Length, MaxArchivePrefix + 8L)];
        input.ReadExactly(prefix);
        var candidates = new List<(ArchiveFormat Format, int Offset)>();
        foreach (var format in Formats)
        {
            int first = int.MaxValue;
            foreach (var signature in format.Signatures)
            {
                int at = prefix.AsSpan().IndexOf(signature);
                if (at == 0) yield break;
                if (at > 0) first = Math.Min(first, at);
            }
            if (first <= MaxArchivePrefix) candidates.Add((format, first));
        }
        foreach (var candidate in candidates.OrderBy(c => c.Offset))
        {
            var format = candidate.Format;
            yield return (format.Name, format.Id, MaxArchivePrefix);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObjectDelegate(ref Guid classId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IInArchive archive);

    public SevenZipEngine(string libraryPath)
    {
        library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        createObject = Marshal.GetDelegateForFunctionPointer<CreateObjectDelegate>(NativeLibrary.GetExport(library, "CreateObject"));
    }

    public void Scan(string path, Action<string, ArchiveIdentity> opened, Action<ArchiveEntry[]> batchReady, string? password = null,
        Func<uint[]?>? selectEntries = null, Action<ScanMessage>? emit = null, ulong byteLimit = 4UL * 1024 * 1024 * 1024, int nameCodePage = 0, bool verify = false, bool checkOnly = false)
    {
        if (nameCodePage is not (0 or 65001 or 936 or 932)) throw new ArchiveEngineException("EncodingNotSupported");
        // Restrict to a local/UNC file path; never pass user-controlled engine switches.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        var stream = new ArchiveInputStream(input);
        foreach (var format in ProbeOrder(input))
        {
            input.Position = 0;
            var classId = new Guid($"23170F69-40C1-278A-1000-000110{format.Id:X2}0000");
            var interfaceId = typeof(IInArchive).GUID;
            Marshal.ThrowExceptionForHR(createObject(ref classId, ref interfaceId, out var archive));
            try
            {
                if (format.Name == "ZIP" && nameCodePage != 0) NativeProperties.Set(archive, ("cp", (uint)nameCodePage));
                var callback = new ArchiveOpenCallback(password, emit);
                ulong maxSearch = format.Search;
                int result = archive.Open(stream, ref maxSearch, callback);
                if (callback.PasswordRequested && password is null) throw new ArchiveEngineException("PasswordRequired");
                if (callback.PasswordRequested && result != 0) throw new ArchiveEngineException("WrongPasswordOrDamaged");
                if (result != 0) continue;
                if (format.Name != "ZIP" && nameCodePage != 0) throw new ArchiveEngineException("EncodingNotSupported");
                Marshal.ThrowExceptionForHR(archive.GetNumberOfItems(out uint count));
                if (count > ArchiveProtocol.MaxEntries) throw new ArchiveEngineException("ResourceLimit");
                opened(format.Name, ArchiveFileIdentity.Read(input.SafeFileHandle));
                var batch = new List<ArchiveEntry>(32);
                long characters = 0;
                for (uint i = 0; i < count; i++)
                {
                    using var name = Property(archive, i, 3);
                    using var directory = Property(archive, i, 6);
                    using var size = Property(archive, i, 7);
                    using var encrypted = Property(archive, i, 15);
                    using var attributes = Property(archive, i, 9);
                    using var posix = Property(archive, i, 53);
                    using var symlink = Property(archive, i, 54);
                    using var hardlink = Property(archive, i, 90);
                    string entryPath = name.Text ?? $"[entry-{i}]";
                    characters += entryPath.Length;
                    if (entryPath.Length > 32768 || characters > ArchiveProtocol.MaxMetadataCharacters)
                        throw new ArchiveEngineException("ResourceLimit");
                    ulong rawAttributes = attributes.Number ?? 0;
                    bool isLink = symlink.Text is not null || hardlink.Text is not null ||
                        (rawAttributes & 0x400) != 0 || ((posix.Number ?? 0) & 0xF000) == 0xA000 ||
                        ((rawAttributes & 0x8000) != 0 && ((rawAttributes >> 16) & 0xF000) == 0xA000);
                    batch.Add(new ArchiveEntry(i, entryPath, directory.Boolean, size.Number, encrypted.Boolean, isLink));
                    // Small bounded frames; a single long path cannot push the batch beyond the IPC limit.
                    if (batch.Count == 32 || batch.Sum(e => e.Path.Length) > 32000)
                    {
                        batchReady(batch.ToArray());
                        batch.Clear();
                    }
                }
                if (batch.Count > 0) batchReady(batch.ToArray());
                if (verify)
                {
                    var checker = new VerificationCallback(password, emit ?? (_ => { }));
                    if (archive.Extract(null, uint.MaxValue, 1, checker) != 0 || checker.Failed)
                        throw new ArchiveEngineException("IntegrityFailed");
                }
                if (selectEntries is not null && emit is not null)
                {
                    uint[]? selection = selectEntries();
                    if (selection?.Length != 0)
                    {
                        var extraction = new ExtractionCallback(emit, password, byteLimit, checkOnly);
                        int extractionResult = archive.Extract(selection, selection is null ? uint.MaxValue : (uint)selection.Length, 0, extraction);
                        if (extraction.ErrorCode is not null) throw new ArchiveEngineException(extraction.ErrorCode);
                        if (extractionResult != 0) throw new ArchiveEngineException("ReadFailed");
                        extraction.FlushChecks();
                    }
                }
                return;
            }
            finally
            {
                archive.Close();
                Marshal.FinalReleaseComObject(archive);
                GC.KeepAlive(stream);
            }
        }
        throw new ArchiveEngineException("UnsupportedOrDamaged");
    }

    private static PropVariant Property(IInArchive archive, uint index, uint property)
    {
        Marshal.ThrowExceptionForHR(archive.GetProperty(index, property, out var value));
        return value;
    }
    public void Dispose() => NativeLibrary.Free(library);
}
