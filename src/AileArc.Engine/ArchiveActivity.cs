using System.Runtime.InteropServices;
using AileArc.Shared;

namespace AileArc.Engine;

// Opening headers and decoding the prefix of a solid archive can both precede
// the first output entry. Report native advancement without inventing a total.
internal sealed class ArchiveActivity(Action<ScanMessage>? emit)
{
    private ulong lastFiles, lastBytes;
    private long lastReport;

    internal int Report(IntPtr files, IntPtr bytes)
    {
        if (emit is null) return 0;
        lock (this)
        {
            long now = Environment.TickCount64;
            if (now - lastReport < 1000) return 0;
            ulong fileCount = files == IntPtr.Zero ? 0 : (ulong)Marshal.ReadInt64(files);
            ulong byteCount = bytes == IntPtr.Zero ? 0 : (ulong)Marshal.ReadInt64(bytes);
            if (fileCount <= lastFiles && byteCount <= lastBytes) return 0;
            lastReport = now;
            lastFiles = Math.Max(lastFiles, fileCount);
            lastBytes = Math.Max(lastBytes, byteCount);
            try { emit(new("activity")); return 0; }
            catch (IOException) { return unchecked((int)0x80004004); }
        }
    }
}
