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
public sealed class ExtractionCallback(Action<ScanMessage> emit, string? password, ulong byteLimit)
    : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private long? current;
    private ulong written;
    private readonly ulong limit = byteLimit;
    private readonly Action<ScanMessage> sender = emit;
    public string? ErrorCode { get; private set; }
    public int SetTotal(ulong total) => 0;
    public int SetCompleted(IntPtr completed) => 0;
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
            sender(new ScanMessage("file", EntryId: index));
            stream = new Output(this);
            return 0;
        }
        catch (IOException) { return unchecked((int)0x80004004); }
    }
    public int SetOperationResult(int result)
    {
        try
        {
            if (current is not null) sender(new ScanMessage("fileDone", EntryId: current, Result: result));
            current = null;
            return 0;
        }
        catch (IOException) { return unchecked((int)0x80004004); }
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
                    owner.ErrorCode = "ResourceLimit";
                    return unchecked((int)0x80004004);
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
