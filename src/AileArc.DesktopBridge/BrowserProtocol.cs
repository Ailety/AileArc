using System.Text.Json;
using System.Text.Json.Serialization;
using AileArc.Core;

namespace AileArc.DesktopBridge;

public sealed record BrowserRequest(int Version, string Id, string Action, string Directory = "", string Search = "",
    string Sort = "name", bool Descending = false, int Offset = 0, string? Password = null, int CodePage = 0);
public static class BrowserProtocol
{
    public const string Origin = "https://ailearc.local";
    public const string Document = Origin + "/index.html";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static bool TrustedDocument(string? value) => value == Document;
    public static bool TrustedResource(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "ailearc.local" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        !uri.AbsolutePath.Contains('%') && !uri.AbsolutePath.Contains('\\') && uri.Query.Length == 0 &&
        (uri.AbsolutePath == "/index.html" || uri.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal));
    public static BrowserRequest Parse(string source, string json)
    {
        if (!TrustedDocument(source) || json.Length > 128 * 1024) throw new InvalidDataException("Invalid browser request.");
        var request = JsonSerializer.Deserialize<BrowserRequest>(json, Json) ?? throw new InvalidDataException();
        if (request.Version != 1 || !Guid.TryParse(request.Id, out _) ||
            request.Action is not ("ready" or "pickArchive" or "query" or "cancel" or "password" or "encoding") ||
            request.Directory is null || request.Search is null || request.Directory.Length > 32768 || request.Search.Length > 512 ||
            request.Sort is not ("name" or "size") || request.Offset < 0 || request.Offset > 500000 ||
            request.Password?.Length > 4096 || request.CodePage is not (0 or 65001 or 936 or 932))
            throw new InvalidDataException("Invalid browser request.");
        return request;
    }
}

public sealed record BrowserRow(string Id, string Name, string Parent, string Path, string Type, double? Size, string Date, bool Encrypted)
{
    [JsonIgnore] public ulong? UnpackedBytes { get; init; }
}
public sealed record BrowserPage(BrowserRow[] Rows, int Total, int Offset, bool HasMore);
public sealed class ArchivePresentation
{
    private const int PageRows = 200;
    private const int PageBytes = 512 * 1024;
    private sealed record OrderKey(string Directory, string Search, bool Local, string Sort, bool Descending, Func<string,string>? TypeLabel);
    private sealed record OrderedView(OrderKey Key, BrowserEntry[] Rows);
    private OrderedView? lastView;
    private long queryVersion;
    public ArchiveIndex Index { get; }
    public ArchiveScan Scan { get; }
    public ArchivePresentation(ArchiveScan scan, CancellationToken token = default)
    {
        // Conservative limits include implicit folders before allocating the index.
        long nodes = 0, characters = 0;
        foreach (var entry in scan.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Path is null || entry.Path.Length > 32768) throw new ArchiveOperationException("ResourceLimit");
            nodes++; characters += entry.Path.Length;
            for (int i = 0; i < entry.Path.Length; i++) if (entry.Path[i] is '/' or '\\') { nodes++; characters += i; }
            if (nodes > 500000 || characters > 64L * 1024 * 1024) throw new ArchiveOperationException("ResourceLimit");
        }
        Scan = scan;
        Index = new ArchiveIndex(scan.Entries, token);
    }
    public BrowserRow[] RootFolders => GetRootFolders();
    public BrowserRow[] GetRootFolders(CancellationToken token = default) => Bound(Index.Query("", cancellationToken: token).Where(row => row.IsDirectory).Take(100), 128 * 1024);
    public BrowserPage Query(BrowserRequest request, CancellationToken token = default, bool currentDirectoryOnly = false, Func<string,string>? typeLabel = null, long? revealId = null, bool previousPage = false)
    {
        token.ThrowIfCancellationRequested();
        long version = Interlocked.Increment(ref queryVersion);
        var key = new OrderKey(request.Directory.TrimEnd('/'), request.Search, currentDirectoryOnly, request.Sort, request.Descending,
            request.Sort == "type" ? typeLabel : null);
        var cached = Volatile.Read(ref lastView);
        var rows = cached?.Key == key ? cached.Rows : Index.Query(key.Directory, key.Search, key.Local,
            sort: key.Sort switch { "size" => EntrySort.Size, "type" => EntrySort.Type, "path" => EntrySort.Path, _ => EntrySort.Name },
            descending: key.Descending, cancellationToken: token, typeLabel: key.TypeLabel);
        token.ThrowIfCancellationRequested();
        if (cached?.Key != key && Volatile.Read(ref queryVersion) == version)
            Volatile.Write(ref lastView, new OrderedView(key, rows));
        int offset = Math.Min(request.Offset, rows.Length);
        int at = previousPage ? Math.Max(0, offset - 1) : offset;
        if (revealId is long id)
        {
            at = Array.FindIndex(rows, r => r.Id == id);
            if (at < 0) throw new ArchiveOperationException("ArchiveChanged");
        }
        // Each block starts on a stable boundary. Long metadata can split it into
        // several pages; reconstruct only this block when going back or locating an ID.
        int blockStart = at / PageRows * PageRows;
        if (previousPage || revealId.HasValue) offset = blockStart;
        int blockEnd = Math.Min(blockStart + PageRows, rows.Length);
        while (true)
        {
            var page = Bound(rows.Skip(offset).Take(blockEnd - offset), PageBytes, token);
            int end = offset + page.Length;
            if (end == offset && offset < rows.Length) throw new ArchiveOperationException("ResourceLimit");
            if (end > at || end == rows.Length) return new(page, rows.Length, offset, end < rows.Length);
            offset = end;
        }
    }
    private static BrowserRow[] Bound(IEnumerable<BrowserEntry> rows, int limit, CancellationToken token = default)
    {
        var result = new List<BrowserRow>(); int bytes = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            string type = row.Kind;
            var item = new BrowserRow(row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Name,
                row.Parent.Length > 0 ? row.Parent + "/" : "", row.Path, type, row.Size is null ? null : row.Size.Value / 1024d, "—", row.Encrypted) { UnpackedBytes = row.Size };
            int length = JsonSerializer.SerializeToUtf8Bytes(item, BrowserProtocol.Json).Length;
            if (length > limit) continue;
            if (bytes + length > limit) break;
            bytes += length; result.Add(item);
        }
        return result.ToArray();
    }
}
