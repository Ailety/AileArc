using System.Runtime.InteropServices;
using AileArc.Shared;

namespace AileArc.Engine;

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveExtractCallback
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completed);
    [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? stream, int askMode);
    [PreserveSig] int PrepareOperation(int askMode);
    [PreserveSig] int SetOperationResult(int result);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
}

/// <summary>Native code receives an output stream, never a destination path or filesystem handle.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractionCallback(Action<ScanMessage> emit, string? password, ulong byteLimit, bool checkOnly = false)
    : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private long? current;
    private ulong written;
    private ulong entryStart;
    private long lastByteReport;
    private readonly List<CheckedEntry>? checks = checkOnly ? [] : null;
    private readonly ulong limit = byteLimit;
    private readonly Action<ScanMessage> sender = emit;
    private readonly ArchiveActivity activity = new(emit);
    public string? ErrorCode { get; private set; }
    public int SetTotal(ulong total) => 0;
    public int SetCompleted(IntPtr completed) => activity.Report(IntPtr.Zero, completed);
    public int PrepareOperation(int askMode) => 0;
    public int CryptoGetTextPassword(out string? value)
    {
        value = password;
        if (value is not null) return 0;
        ErrorCode = "PasswordRequired";
        return unchecked((int)0x80004004);
    }
    public int GetStream(uint index, out ISequentialOutStream? stream, int askMode)
    {
        stream = null;
        current = null;
        if (askMode != 0) return 0;
        try
        {
            current = index;
            entryStart = written;
            if (checks is null) sender(new ScanMessage("file", EntryId: index));
            stream = new Output(this);
            return 0;
        }
        catch (IOException) { return unchecked((int)0x80004004); }
    }
    public int SetOperationResult(int result)
    {
        try
        {
            if (current is long id)
            {
                if (checks is null) sender(new ScanMessage("fileDone", EntryId: id, Result: result));
                else
                {
                    checks.Add(new(id, result, written - entryStart));
                    if (checks.Count == 128) FlushChecks();
                }
            }
            current = null;
            return 0;
        }
        catch (IOException) { return unchecked((int)0x80004004); }
    }
    internal void FlushChecks()
    {
        if (checks is not { Count: > 0 }) return;
        sender(new ScanMessage("checked", Checked: checks.ToArray()));
        checks.Clear();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Output(ExtractionCallback owner) : ISequentialOutStream
    {
        public int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
            try
            {
                if (owner.written + size > owner.limit || owner.current is null)
                {
                    owner.ErrorCode = owner.current is null ? "ProtocolMismatch" : owner.checks is null ? "ExtractionByteLimit" : "TestByteLimit";
                    return unchecked((int)0x80004004);
                }
                if (owner.checks is not null)
                {
                    owner.written += size;
                    long now = Environment.TickCount64;
                    if (now - owner.lastByteReport >= 200)
                    {
                        owner.lastByteReport = now;
                        owner.sender(new("testing", CompletedBytes: owner.written));
                    }
                    if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, unchecked((int)size));
                    return 0;
                }
                uint offset = 0;
                while (offset < size)
                {
                    int length = (int)Math.Min(32 * 1024U, size - offset);
                    var bytes = new byte[length];
                    Marshal.Copy(IntPtr.Add(data, checked((int)offset)), bytes, 0, length);
                    owner.sender(new ScanMessage("data", EntryId: owner.current, Data: bytes));
                    offset += (uint)length;
                    owner.written += (uint)length;
                }
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, checked((int)size));
                return 0;
            }
            catch (Exception error) when (error is IOException or OverflowException) { return unchecked((int)0x80004004); }
        }
    }
}
