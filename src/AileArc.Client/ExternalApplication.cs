using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AileArc.Client;

internal static class ExternalApplication
{
    internal static void Open(string file, IntPtr owner)
        => Open(file, owner, StartAssociated, ChooseApplication);

    private static void StartAssociated(string file)
    {
        using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private static int ChooseApplication(string file, IntPtr owner)
    {
        var info = new OpenAsInfo { File = file, Flags = 4 }; // OAIF_EXEC: open this file.
        return SHOpenWithDialog(owner, ref info);
    }

    internal static void Open(string file, IntPtr owner, Action<string> start, Func<string, IntPtr, int> choose)
    {
        try { start(file); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { throw new OperationCanceledException(); }
        catch (Win32Exception error) when (error.NativeErrorCode is 1155 or 31)
        {
            int result = choose(file, owner);
            if (result == unchecked((int)0x800704C7)) throw new OperationCanceledException();
            if (result != 0) throw new COMException("Open With did not open the working copy.", result);
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
