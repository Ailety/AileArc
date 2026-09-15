using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Core.Extraction;

/// <summary>Walk and pin ancestors without following reparse points. Held handles deny write/delete sharing.</summary>
internal sealed class DirectoryGuard : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    public static DirectoryGuard Acquire(string path, bool create)
    {
        var guard = new DirectoryGuard();
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? throw new ArchiveOperationException("UnsafePath");
            // This iteration supports local fixed/removable drives; network destination semantics require separate validation.
            if (root.StartsWith("\\") || root.StartsWith("\\?")) throw new ArchiveOperationException("DestinationNotSupported");
            string current = root;
            guard.Pin(current);
            foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (create && !Directory.Exists(current))
                {
                    if (!CreateDirectory(current, IntPtr.Zero) && Marshal.GetLastWin32Error() != 183)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                guard.Pin(current);
            }
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    private void Pin(string path)
    {
        var handle = CreateFile(path, 0x80, 1, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        handles.Add(handle);
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
            throw new ArchiveOperationException("UnsafePath");
    }
    public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInfo { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    internal static extern bool CreateDirectory(string path, IntPtr security);
}
