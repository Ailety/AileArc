using System.Runtime.InteropServices;
using AileArc.Engine;
using AileArc.Shared;

// Stdout is exclusively framed protocol, never diagnostic text. One process handles one scan.
using var output = Console.OpenStandardOutput();
using var input = Console.OpenStandardInput();
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var request = await ArchiveProtocol.ReadAsync<ScanRequest>(input, timeout.Token);
    if (request.Version != ArchiveProtocol.Version) throw new ArchiveEngineException("ProtocolMismatch");
    if (request.Operation is not ("scan" or "extract" or "create")) throw new ArchiveEngineException("ProtocolMismatch");
    uint[]? SelectEntries()
    {
        ArchiveProtocol.WriteAsync(output, new ScanMessage("ready")).GetAwaiter().GetResult();
        return ArchiveProtocol.ReadAsync<ExtractionCommand>(input).GetAwaiter().GetResult().EntryIds;
    }
    using var engine = new SevenZipEngine(Path.Combine(AppContext.BaseDirectory, "7z.dll"));
    if (request.Operation == "create")
    {
        if (request.CompressionCount is < 1 or > 10000) throw new ArchiveEngineException("ResourceLimit");
        var items = new List<CompressionItem>();
        long characters = 0;
        while (items.Count < request.CompressionCount)
        {
            var batch = await ArchiveProtocol.ReadAsync<CompressionBatch>(input, timeout.Token);
            if (batch.Items.Length is 0 or > 32) throw new ArchiveEngineException("ProtocolMismatch");
            foreach (var item in batch.Items)
            {
                characters += item.SourcePath.Length + item.EntryPath.Length;
                if (items.Count >= request.CompressionCount || characters > 4 * 1024 * 1024) throw new ArchiveEngineException("ResourceLimit");
                items.Add(item);
            }
        }
        engine.CreateArchive(request, items, message => ArchiveProtocol.WriteAsync(output, message).GetAwaiter().GetResult());
        await ArchiveProtocol.WriteAsync(output, new ScanMessage("completed"));
        return 0;
    }
    engine.Scan(request.Path,
        (format, identity) => ArchiveProtocol.WriteAsync(output, new ScanMessage("opened", Format: format, Identity: identity)).GetAwaiter().GetResult(),
        batch => ArchiveProtocol.WriteAsync(output, new ScanMessage("entries", Entries: batch)).GetAwaiter().GetResult(),
        request.Password, request.Operation == "extract" ? SelectEntries : null,
        message => ArchiveProtocol.WriteAsync(output, message).GetAwaiter().GetResult(), request.ByteLimit, request.NameCodePage);
    await ArchiveProtocol.WriteAsync(output, new ScanMessage("completed"));
    return 0;
}
catch (Exception error)
{
    string code = error switch
    {
        ArchiveEngineException engine => engine.Code,
        FileNotFoundException or DirectoryNotFoundException => "FileNotFound",
        UnauthorizedAccessException => "AccessDenied",
        DllNotFoundException or BadImageFormatException => "EngineUnavailable",
        IOException => "ReadFailed",
        COMException => "UnsupportedOrDamaged",
        _ => "WorkerFailed"
    };
    try { await ArchiveProtocol.WriteAsync(output, new ScanMessage("error", ErrorCode: code)); }
    catch (IOException) { /* The parent cancelled and closed its pipes. */ }
    return 1;
}
