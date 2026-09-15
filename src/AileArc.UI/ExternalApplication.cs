using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AileArc.UI;

internal static class ExternalApplication
{
    public static void Open(string file, IntPtr owner)
    {
        try { using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Win32Exception error) when (error.NativeErrorCode is 1155 or 31)
        {
            var info = new OpenAsInfo { File = file, Flags = 4 }; // OAIF_EXEC: execute selection without setting a new default.
            int result = SHOpenWithDialog(owner, ref info);
            if (result < 0 && result != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(result);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public uint Flags;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr window, ref OpenAsInfo info);
}
