using System.Runtime.InteropServices;
using AileArc.Shared;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Engine;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class CompressionCallback(IReadOnlyList<CompressionItem> items, string? password, Action<ScanMessage> report)
    : IArchiveUpdateCallback, ICryptoGetTextPassword2, IDisposable
{
    private FileStream? current;
    private readonly List<FileStream> opened = [];
    private ulong total;
    private long lastReport;
    public string? ErrorCode { get; private set; }
    public int SetTotal(ulong value) { total = value; return 0; }
    public int SetCompleted(IntPtr completed)
    {
        if (Environment.TickCount64 - lastReport < 100) return 0;
        lastReport = Environment.TickCount64;
        try { report(new ScanMessage("compressing", CompletedBytes: completed == IntPtr.Zero ? 0 : (ulong)Marshal.ReadInt64(completed), TotalBytes: total)); return 0; }
        catch (IOException) { return unchecked((int)0x80004004); }
    }
    public int GetUpdateItemInfo(uint index, out int newData, out int newProperties, out uint indexInArchive)
    { newData = 1; newProperties = 1; indexInArchive = uint.MaxValue; return 0; }
    public int GetProperty(uint index, uint property, out PropVariant value)
    {
        var item = items[checked((int)index)];
        value = property switch
        {
            3 => new() { Type = 8, Pointer = Marshal.StringToBSTR(item.EntryPath) },
            6 => new() { Type = 11, Signed = item.IsDirectory ? -1 : 0 },
            7 => new() { Type = 21, Unsigned = (ulong)item.Length },
            9 => new() { Type = 19, Unsigned = item.IsDirectory ? 16U : 32U },
            12 => new() { Type = 64, Unsigned = (ulong)item.LastWriteTime },
            _ => default
        };
        return 0;
    }
    public int GetStream(uint index, out ISequentialInStream? stream)
    {
        current = null; stream = null;
        var item = items[checked((int)index)];
        if (item.IsDirectory) return 0;
        try
        {
            var handle = CreateFile(item.SourcePath, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException(); }
            try
            {
                if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8) || (attributes.Attributes & (0x400 | 0x10)) != 0)
                    throw new IOException();
                var identity = ArchiveFileIdentity.Read(handle);
                if (identity.Length != item.Length || identity.LastWriteTime != item.LastWriteTime) throw new IOException();
                current = new FileStream(handle, FileAccess.Read);
                opened.Add(current);
            }
            catch { handle.Dispose(); throw; }
            stream = new ArchiveInputStream(current);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { ErrorCode = "SourceChanged"; return unchecked((int)0x80004004); }
    }
    public int SetOperationResult(int result) { if (result != 0) ErrorCode = "CompressionFailed"; return 0; }
    public int CryptoGetTextPassword2(out int defined, out string? value) { value = password; defined = password is null ? 0 : 1; return 0; }
    public void Dispose() { foreach (var file in opened) file.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeInfo info, uint size);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class CompressionOutput(FileStream file, ulong limit) : ISequentialOutStream, IOutStream
{
    private readonly byte[] buffer = new byte[65536];
    public int Write(IntPtr data, uint size, IntPtr processed)
    {
        if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, 0);
        try
        {
            if ((ulong)file.Position + size > limit) return unchecked((int)0x80070070);
            uint offset = 0;
            while (offset < size)
            {
                int count = (int)Math.Min(size - offset, (uint)buffer.Length);
                Marshal.Copy(IntPtr.Add(data, checked((int)offset)), buffer, 0, count);
                file.Write(buffer, 0, count);
                offset += (uint)count;
            }
            if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, checked((int)size));
            return 0;
        }
        catch (IOException) { return unchecked((int)0x8007001D); }
    }
    public int Seek(long offset, uint origin, IntPtr newPosition)
    {
        try
        {
            if (origin > 2) return unchecked((int)0x80070057);
            long position = file.Seek(offset, (SeekOrigin)origin);
            if ((ulong)position > limit) return unchecked((int)0x80070070);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
            return 0;
        }
        catch (IOException) { return unchecked((int)0x80070019); }
    }
    public int SetSize(ulong size)
    {
        try { if (size > limit) return unchecked((int)0x80070070); file.SetLength((long)size); return 0; }
        catch (IOException) { return unchecked((int)0x8007001D); }
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class VerificationCallback(string? password, Action<ScanMessage> report) : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private long lastReport;
    public bool Failed { get; private set; }
    public int SetTotal(ulong total) => 0;
    public int SetCompleted(IntPtr completed)
    {
        if (Environment.TickCount64 - lastReport < 100) return 0;
        lastReport = Environment.TickCount64;
        try { report(new ScanMessage("verifying")); return 0; }
        catch (IOException) { return unchecked((int)0x80004004); }
    }
    public int GetStream(uint index, out ISequentialOutStream? stream, int askMode) { stream = null; return 0; }
    public int PrepareOperation(int askMode) => 0;
    public int SetOperationResult(int result) { if (result != 0) Failed = true; return 0; }
    public int CryptoGetTextPassword(out string? value) { value = password; return value is null ? unchecked((int)0x80004004) : 0; }
}
