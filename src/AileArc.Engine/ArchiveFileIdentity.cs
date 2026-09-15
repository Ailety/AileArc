using System.ComponentModel;
using System.Runtime.InteropServices;
using AileArc.Shared;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Engine;

internal static class ArchiveFileIdentity
{
    public static ArchiveIdentity Read(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new($"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}",
            (long)(((ulong)info.SizeHigh << 32) | info.SizeLow),
            (long)(((ulong)info.WriteHigh << 32) | info.WriteLow));
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
}
