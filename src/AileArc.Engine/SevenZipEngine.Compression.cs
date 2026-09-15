using System.Runtime.InteropServices;
using AileArc.Shared;

namespace AileArc.Engine;

public sealed partial class SevenZipEngine
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateOutDelegate(ref Guid classId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IOutArchive archive);

    public void CreateArchive(ScanRequest request, IReadOnlyList<CompressionItem> items, Action<ScanMessage> report)
    {
        if (request.CompressionFormat is not ("ZIP" or "7Z") || request.CompressionPreset is not ("Fast" or "Balanced" or "Maximum"))
            throw new ArchiveEngineException("CompressionOptionsInvalid");
        if (request.ByteLimit > 4UL * 1024 * 1024 * 1024 || items.Count > 10000) throw new ArchiveEngineException("ResourceLimit");
        uint level = request.CompressionPreset switch { "Fast" => 1, "Maximum" => 9, _ => 5 };
        var classId = new Guid(request.CompressionFormat == "ZIP" ? "23170F69-40C1-278A-1000-000110010000" : "23170F69-40C1-278A-1000-000110070000");
        var iid = typeof(IOutArchive).GUID;
        var factory = Marshal.GetDelegateForFunctionPointer<CreateOutDelegate>(NativeLibrary.GetExport(library, "CreateObject"));
        Marshal.ThrowExceptionForHR(factory(ref classId, ref iid, out var archive));
        try
        {
            var options = new List<(string Name, object Value)> { ("x", level), ("mt", 2U) };
            if (request.CompressionFormat == "ZIP")
            {
                options.Add(("m", "Deflate"));
                options.Add(("cu", true));
                if (request.Password is not null) options.Add(("em", "AES256"));
            }
            else
            {
                options.Add(("0", "LZMA2"));
                options.Add(("0d", request.CompressionPreset switch { "Fast" => "4m", "Maximum" => "32m", _ => "16m" }));
                options.Add(("s", true));
                options.Add(("he", request.EncryptNames && request.Password is not null));
            }
            NativeProperties.Set(archive, options.ToArray());
            using var callback = new CompressionCallback(items, request.Password, report);
            using (var file = new FileStream(request.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (file.Length != 0) throw new ArchiveEngineException("CompressionOptionsInvalid");
                var output = new CompressionOutput(file, request.ByteLimit + 64UL * 1024 * 1024);
                int result = archive.UpdateItems(output, (uint)items.Count, callback);
                if (callback.ErrorCode is not null) throw new ArchiveEngineException(callback.ErrorCode);
                if (result != 0) throw new ArchiveEngineException("CompressionFailed");
                file.Flush(flushToDisk: true);
                GC.KeepAlive(output);
            }
        }
        finally { Marshal.FinalReleaseComObject(archive); }
        report(new ScanMessage("verifying"));
        Scan(request.Path, (_, _) => { }, _ => { }, request.Password, emit: report, verify: true);
    }
}
