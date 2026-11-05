using System.ComponentModel;
using System.Diagnostics;
using AileArc.Shared;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Core.Storage;

/// <summary>Pin a regular file without following links and delete through that exact handle, never through a re-resolved path.</summary>
internal sealed class VerifiedFile : IDisposable
{
    public FileStream Stream { get; }
    public string Identity { get; }
    private string currentPath;
    public VerifiedFile(string path) : this(path, false) { }
    internal static VerifiedFile OpenStaging(string path) => new(path, true);
    internal static VerifiedFile OpenAsyncStaging(string path) => new(path, true, asynchronous: true);
    internal static VerifiedFile CreateStaging(string path) => new(path, true, createNew: true);
    private VerifiedFile(string path, bool writable, bool createNew = false, bool asynchronous = false)
    {
        currentPath = Path.GetFullPath(path);
        uint flags = 0x00200000 | 0x02000000;
        if (asynchronous) flags |= 0x40000000;
        var handle = CreateFile(currentPath, writable ? 0xC0010000U : 0x80010000U, writable ? 0U : 1U, IntPtr.Zero, createNew ? 1U : 3U, flags, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            var info = Information(handle);
            if ((info.Attributes & (0x400 | 0x10)) != 0 || info.Links != 1) throw new ArchiveOperationException("UnsafePath");
            Identity = Key(info);
            Stream = new FileStream(handle, createNew || asynchronous ? FileAccess.ReadWrite : FileAccess.Read, asynchronous ? 65536 : 4096, asynchronous);
        }
        catch { handle.Dispose(); throw; }
    }
    public void DeleteOnClose()
    {
        int delete = 1;
        if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, ref delete, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal void RenameTo(string destination, bool replace)
    {
        // FILE_RENAME_INFO has an aligned root HANDLE followed by the UTF-16 byte length.
        string full = Path.GetFullPath(destination);
        byte[] name = System.Text.Encoding.Unicode.GetBytes(full);
        int lengthOffset = 2 * IntPtr.Size, nameOffset = lengthOffset + sizeof(uint);
        var buffer = new byte[nameOffset + name.Length + sizeof(char)];
        buffer[0] = replace ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(buffer.AsSpan(lengthOffset), name.Length);
        name.CopyTo(buffer, nameOffset);
        if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, buffer, (uint)buffer.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        currentPath = full;
    }
    internal async Task SetDownloadZoneAsync(string zoneId, CancellationToken token)
    {
        if (zoneId is not ("3" or "4")) throw new ArchiveOperationException("InvalidSourceZone");
        // The base handle pins the name. The ADS must share its delete access.
        await using var mark = new FileStream(currentPath + ":Zone.Identifier", FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await mark.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"[ZoneTransfer]\r\nZoneId={zoneId}\r\n"), token);
        mark.Flush(flushToDisk: true);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, uint size);
    public void Dispose() => Stream.Dispose();
    internal long DuplicateReadWriteTo(Process worker)
    {
        // The returned value belongs to the child process. It has read/write
        // access to this file, but not the parent's right to rename or delete it.
        const uint fileGenericReadWrite = 0x12019f;
        if (!DuplicateHandle(new IntPtr(-1), Stream.SafeFileHandle, worker.SafeHandle, out var child,
            fileGenericReadWrite, false, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return child.ToInt64();
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, SafeProcessHandle targetProcess,
        out IntPtr target, uint access, bool inherit, uint options);
    internal static SafeFileHandle OpenRegularInput(string path, FileShare sharing)
    {
        var handle = CreateFile(path, 0x80000000, (uint)sharing, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if ((Information(handle).Attributes & (0x400 | 0x10)) != 0) throw new ArchiveOperationException("UnsafePath");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    // Open the file itself, without following a replacement link. Deny writes and
    // renames while hashing or exporting this particular version of the payload.
    public static FileStream OpenRead(string path)
    {
        var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | 0x40000000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            throw new IOException(error.Message, error);
        }
        try
        {
            var info = Information(handle);
            if ((info.Attributes & (0x400 | 0x10)) != 0 || info.Links != 1) throw new ArchiveOperationException("UnsafePath");
            return new FileStream(handle, FileAccess.Read, 65536, isAsync: true);
        }
        catch { handle.Dispose(); throw; }
    }
    public static string? TryIdentity(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return IdentityOf(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { return null; }
    }
    public static string IdentityOf(SafeFileHandle handle) => Key(Information(handle));
    internal static (string Id, long Length, long Written, long Changed)? TryChangeStamp(FileStream stream)
    {
        var id = ArchiveFileIdentity.Read(stream.SafeFileHandle);
        return id.ChangeTime is long changed ? (id.FileId, id.Length, id.LastWriteTime, changed) : null;
    }
    private static string Key(Info info) => $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    private static Info Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Info
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref int info, uint size);
}
