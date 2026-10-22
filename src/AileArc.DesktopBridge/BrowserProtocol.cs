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

public sealed record BrowserRow(string Id, string Name, string Parent, string Path, string Type, double? Size, string Date, bool Encrypted);
public sealed record BrowserPage(BrowserRow[] Rows, int Total, int Offset, bool HasMore);
public sealed class ArchivePresentation
{
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
    public BrowserPage Query(BrowserRequest request, CancellationToken token = default)
    {
        var rows = Index.Query(request.Directory.TrimEnd('/'), request.Search, sort: request.Sort == "size" ? EntrySort.Size : EntrySort.Name,
            descending: request.Descending, cancellationToken: token);
        int offset = Math.Min(request.Offset, rows.Length);
        var page = Bound(rows.Skip(offset).Take(200), 512 * 1024);
        return new(page, rows.Length, offset, offset + page.Length < rows.Length);
    }
    private static BrowserRow[] Bound(IEnumerable<BrowserEntry> rows, int limit)
    {
        var result = new List<BrowserRow>(); int bytes = 0;
        foreach (var row in rows)
        {
            string extension = System.IO.Path.GetExtension(row.Name).ToLowerInvariant();
            string type = row.IsDirectory ? "folder" : extension switch { ".pdf" => "pdf", ".md" => "md", ".txt" => "text", ".json" or ".yml" or ".cs" or ".xml" => "code", ".png" or ".jpg" or ".svg" => "image", _ => "file" };
            var item = new BrowserRow(row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Name,
                row.Parent.Length > 0 ? row.Parent + "/" : "", row.Path, type, row.Size is null ? null : row.Size.Value / 1024d, "—", row.Encrypted);
            int length = JsonSerializer.SerializeToUtf8Bytes(item, BrowserProtocol.Json).Length;
            if (length > limit) continue;
            if (bytes + length > limit) break;
            bytes += length; result.Add(item);
        }
        return result.ToArray();
    }
}
