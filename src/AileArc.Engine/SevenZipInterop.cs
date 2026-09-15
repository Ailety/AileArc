using System.Runtime.InteropServices;

namespace AileArc.Engine;

// ABI definitions: 7-Zip CPP/7zip/IStream.h and CPP/7zip/Archive/IArchive.h.
[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, ref ulong maxCheckStartPosition, IArchiveOpenCallback callback);
    [PreserveSig] int Close();
    [PreserveSig] int GetNumberOfItems(out uint count);
    [PreserveSig] int GetProperty(uint index, uint propertyId, out PropVariant value);
    [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[]? indices, uint count, int testMode, IArchiveExtractCallback callback);
    [PreserveSig] int GetArchiveProperty(uint propertyId, out PropVariant value);
    [PreserveSig] int GetNumberOfProperties(out uint count);
    [PreserveSig] int GetPropertyInfo(uint index, IntPtr name, IntPtr propertyId, IntPtr variantType);
    [PreserveSig] int GetNumberOfArchiveProperties(out uint count);
    [PreserveSig] int GetArchivePropertyInfo(uint index, IntPtr name, IntPtr propertyId, IntPtr variantType);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint origin, IntPtr newPosition);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
    [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string? password);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ArchiveOpenCallback(string? suppliedPassword = null) : IArchiveOpenCallback, ICryptoGetTextPassword
{
    public bool PasswordRequested { get; private set; }
    public int SetTotal(IntPtr files, IntPtr bytes) => 0;
    public int SetCompleted(IntPtr files, IntPtr bytes) => 0;
    public int CryptoGetTextPassword(out string? password)
    {
        PasswordRequested = true;
        password = suppliedPassword;
        return password is null ? unchecked((int)0x80004004) : 0;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ArchiveInputStream(FileStream stream) : IInStream
{
    private readonly byte[] buffer = new byte[64 * 1024];
    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            int count = stream.Read(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
            Marshal.Copy(buffer, 0, data, count);
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, count);
            return 0;
        }
        catch (IOException) { return unchecked((int)0x8007001E); }
    }
    public int Seek(long offset, uint origin, IntPtr newPosition)
    {
        try
        {
            if (origin > 2) return unchecked((int)0x80070057);
            long position = stream.Seek(offset, (SeekOrigin)origin);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
            return 0;
        }
        catch (IOException) { return unchecked((int)0x80070019); }
    }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant : IDisposable
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(8)] public ulong Unsigned;
    [FieldOffset(8)] public int Signed;
    public readonly string? Text => Type == 8 ? Marshal.PtrToStringBSTR(Pointer) : null;
    public readonly bool Boolean => Type == 11 && Signed != 0;
    public readonly ulong? Number => Type switch { 19 => (uint)Unsigned, 21 => Unsigned, _ => null };
    public void Dispose() => PropVariantClear(ref this);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
