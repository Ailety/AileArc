using System.Runtime.InteropServices;

namespace AileArc.Engine;

[ComImport, Guid("23170F69-40C1-278A-0000-000600A00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutArchive
{
    [PreserveSig] int UpdateItems(ISequentialOutStream stream, uint count, IArchiveUpdateCallback callback);
    [PreserveSig] int GetFileTimeType(out uint type);
}
[ComVisible(true), Guid("23170F69-40C1-278A-0000-000600800000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveUpdateCallback
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completed);
    [PreserveSig] int GetUpdateItemInfo(uint index, out int newData, out int newProperties, out uint indexInArchive);
    [PreserveSig] int GetProperty(uint index, uint property, out PropVariant value);
    [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialInStream? stream);
    [PreserveSig] int SetOperationResult(int result);
}
[ComVisible(true), Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
}
[ComVisible(true), Guid("23170F69-40C1-278A-0000-000300040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint origin, IntPtr newPosition);
    [PreserveSig] int SetSize(ulong size);
}
[ComVisible(true), Guid("23170F69-40C1-278A-0000-000500110000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword2
{
    [PreserveSig] int CryptoGetTextPassword2(out int defined, [MarshalAs(UnmanagedType.BStr)] out string? password);
}
