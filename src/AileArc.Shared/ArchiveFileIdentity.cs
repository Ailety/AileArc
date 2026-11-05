using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Shared;

public static class ArchiveFileIdentity
{
    public static ArchiveIdentity Read(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        long? changeTime = GetFileInformationByHandleEx(file, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()) && basic.Changed > 0
            ? basic.Changed : null;
        return new($"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}",
            (long)(((ulong)info.SizeHigh << 32) | info.SizeLow),
            (long)(((ulong)info.WriteHigh << 32) | info.WriteLow), changeTime);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    {
        public long Created, Accessed, Written, Changed;
        public uint Attributes;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
}
