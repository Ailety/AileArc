using System.Runtime.InteropServices;

namespace AileArc.Engine;

[ComImport, Guid("23170F69-40C1-278A-0000-000600030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISetProperties
{
    [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] PropVariant[] values, uint count);
}

internal static class NativeProperties
{
    public static void Set(object handler, params (string Name, object Value)[] properties)
    {
        var values = properties.Select(p => p.Value switch
        {
            uint number => new PropVariant { Type = 19, Unsigned = number },
            bool flag => new PropVariant { Type = 11, Signed = flag ? -1 : 0 },
            string text => new PropVariant { Type = 8, Pointer = Marshal.StringToBSTR(text) },
            _ => throw new ArgumentException("Unsupported native property type.")
        }).ToArray();
        try { Marshal.ThrowExceptionForHR(((ISetProperties)handler).SetProperties(properties.Select(p => p.Name).ToArray(), values, (uint)values.Length)); }
        finally { for (int i = 0; i < values.Length; i++) values[i].Dispose(); }
    }
}
