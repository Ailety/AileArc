using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Core.Storage;

/// <summary>Pin a regular file without following links and delete through that exact handle, never through a re-resolved path.</summary>
internal sealed class VerifiedFile : IDisposable
{
    public FileStream Stream { get; }
    public string Identity { get; }
    public VerifiedFile(string path)
    {
        var handle = CreateFile(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            var info = Information(handle);
            if ((info.Attributes & (0x400 | 0x10)) != 0 || info.Links != 1) throw new ArchiveOperationException("UnsafePath");
            Identity = Key(info);
            Stream = new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }
    public void DeleteOnClose()
    {
        int delete = 1;
        if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, ref delete, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => Stream.Dispose();
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
