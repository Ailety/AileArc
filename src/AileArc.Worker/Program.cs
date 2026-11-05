using System.Runtime.InteropServices;
using AileArc.Engine;
using AileArc.Shared;

// Stdout is exclusively framed protocol, never diagnostic text. One process handles one scan.
using var output = Console.OpenStandardOutput();
using var input = Console.OpenStandardInput();
var sendLock = new object();
void Send(ScanMessage message)
{
    // Native progress and data callbacks must never interleave the header and
    // body of separate frames on the shared output pipe.
    lock (sendLock) ArchiveProtocol.WriteAsync(output, message).GetAwaiter().GetResult();
}
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var request = await ArchiveProtocol.ReadAsync<ScanRequest>(input, timeout.Token);
    if (request.Version != ArchiveProtocol.Version) throw new ArchiveEngineException("ProtocolMismatch");
    if (request.Operation is not ("scan" or "extract" or "create")) throw new ArchiveEngineException("ProtocolMismatch");
    uint[]? SelectEntries()
    {
        Send(new("ready"));
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
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var batch = await ArchiveProtocol.ReadAsync<CompressionBatch>(input, timeout.Token);
            if (batch.Items.Length is 0 or > 32) throw new ArchiveEngineException("ProtocolMismatch");
            foreach (var item in batch.Items)
            {
                characters += item.SourcePath.Length + item.EntryPath.Length;
                if (items.Count >= request.CompressionCount || characters > 4 * 1024 * 1024) throw new ArchiveEngineException("ResourceLimit");
                items.Add(item);
            }
        }
        engine.CreateArchive(request, items, Send);
        Send(new("completed"));
        return 0;
    }
    engine.Scan(request.Path,
        (format, identity) => Send(new("opened", Format: format, Identity: identity)),
        batch => Send(new("entries", Entries: batch)),
        request.Password, request.Operation == "extract" ? SelectEntries : null,
        Send, request.ByteLimit, request.NameCodePage);
    Send(new("completed"));
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
        InvalidDataException => "ProtocolMismatch",
        IOException => "ReadFailed",
        COMException => "UnsupportedOrDamaged",
        _ => "WorkerFailed"
    };
    try { Send(new("error", ErrorCode: code)); }
    catch (IOException) { /* The parent cancelled and closed its pipes. */ }
    return 1;
}
