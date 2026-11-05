using System.Text.Json;
using AileArc.Core;
using AileArc.DesktopBridge;
using AileArc.Shared;
using Xunit;

namespace AileArc.Tests;
public sealed class DesktopBridgeTests
{
    private static BrowserRequest Query(int offset = 0, string search = "") => new(1, Guid.NewGuid().ToString(), "query", Search: search, Offset: offset);
    [Theory]
    [InlineData("https://ailearc.local.evil/index.html")]
    [InlineData("https://ailearc.local:444/index.html")]
    [InlineData("file:///index.html")]
    [InlineData("https://ailearc.local/index.html?x=1")]
    public void MessagesRequireTheExactApplicationDocument(string source)
        => Assert.Throws<InvalidDataException>(() => BrowserProtocol.Parse(source, JsonSerializer.Serialize(Query(), BrowserProtocol.Json)));
    [Fact]
    public void ProtocolRejectsCommandsPathsAndUnboundedRequests()
    {
        Assert.Equal("query", BrowserProtocol.Parse(BrowserProtocol.Document, JsonSerializer.Serialize(Query(), BrowserProtocol.Json)).Action);
        foreach(var request in new[] { Query() with { Action = "execute" }, Query() with { Search = new string('a',513) }, Query() with { Offset = -1 }, Query() with { CodePage = 1252 }, Query() with { Password = new string('a',4097) } })
            Assert.Throws<InvalidDataException>(()=>BrowserProtocol.Parse(BrowserProtocol.Document,JsonSerializer.Serialize(request,BrowserProtocol.Json)));
        Assert.Throws<JsonException>(()=>BrowserProtocol.Parse(BrowserProtocol.Document,"{\"version\":1,\"id\":\""+Guid.NewGuid()+"\",\"action\":\"pickArchive\",\"path\":\"C:\\\\private.txt\"}"));
        Assert.False(BrowserProtocol.TrustedResource("https://ailearc.local/user-files/secret.txt"));
        Assert.False(BrowserProtocol.TrustedResource("https://ailearc.local/assets/%2f..%2fsecret.txt"));
        Assert.True(BrowserProtocol.TrustedResource("https://ailearc.local/assets/app.js"));
    }
    [Fact]
    public void PagingPreservesDuplicateEntryIdsAndLimitsRows()
    {
        var rows = Enumerable.Range(0,450).Select(i=>new ArchiveEntry(i,"same.txt",false,(ulong)i,false)).ToArray();
        var model = new ArchivePresentation(new("ZIP",rows));
        var first=model.Query(Query()); var next=model.Query(Query(first.Rows.Length)); var last=model.Query(Query(400));
        Assert.Equal(200,first.Rows.Length); Assert.True(first.HasMore); Assert.Equal(450,first.Total);
        Assert.Equal("0",first.Rows[0].Id); Assert.Equal("200",next.Rows[0].Id); Assert.Equal(50,last.Rows.Length); Assert.False(last.HasMore);
    }
    [Fact]
    public void PagesAreByteBoundedAndNamesRemainPlainData()
    {
        var rows=Enumerable.Range(0,20).Select(i=>new ArchiveEntry(i,new string('中',20000)+i+".txt",false,2,false)).ToArray();
        var page=new ArchivePresentation(new("ZIP",rows)).Query(Query());
        Assert.True(page.Rows.Length<20); Assert.True(page.HasMore);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(page,BrowserProtocol.Json).Length<530000);
        var model=new ArchivePresentation(new("ZIP",[new(1,"docs/<img src=x onerror=alert(1)>.txt",false,3,false)]));
        Assert.Single(model.RootFolders);
        Assert.Contains("<img",model.Query(Query(search:"onerror")).Rows[0].Name);
        Assert.DoesNotContain("<img",JsonSerializer.Serialize(model.Query(Query(search:"onerror")),BrowserProtocol.Json));
    }
    [Fact]
    public void PagingReusesOrderButRespectsNewSortScopeAndCancellation()
    {
        var entries = Enumerable.Range(0,450).Select(i=>new ArchiveEntry(i,i%2==0 ? $"a/{i}.md" : $"b/{i}.txt",false,(ulong)i,false)).ToArray();
        var model = new ArchivePresentation(new("ZIP",entries));
        int comparisons = 0;
        string Label(string kind) { comparisons++; return kind; }
        var query = Query(search:".") with { Sort="type" };
        var first = model.Query(query, typeLabel:Label);
        int cold = comparisons;
        var next = model.Query(query with { Offset=200 }, typeLabel:Label);
        Assert.True(cold > 0); Assert.Equal(450,next.Total); Assert.Equal(cold,comparisons);
        Assert.Empty(first.Rows.Select(r=>r.Id).Intersect(next.Rows.Select(r=>r.Id)));
        Assert.Throws<OperationCanceledException>(()=>model.Query(query,new CancellationToken(true),typeLabel:Label));
        Assert.All(model.Query(query with { Descending=true },typeLabel:Label).Rows,r=>Assert.Equal("text",r.Type));
        Assert.Equal(225,model.Query(query with { Directory="a" },currentDirectoryOnly:true,typeLabel:Label).Total);
    }
    [Fact]
    public void LocateWorksWhenMetadataBudgetShortensTheContainingPage()
    {
        string common = new string('文',1000);
        var entries = Enumerable.Range(0,100).Select(i=>new ArchiveEntry(i,common+i.ToString("D3"),false,0,false)).ToArray();
        var model = new ArchivePresentation(new("ZIP",entries));
        Assert.True(model.Query(Query()).Rows.Length < 90);
        Assert.Contains(model.Query(Query(),revealId:90).Rows,r=>r.Id=="90");
    }
    [Fact]
    public void ByteLimitedPagesRoundTripAndLocateUsesTheSameBoundaries()
    {
        string prefix = new('资', 750);
        var entries = Enumerable.Range(0, 423).Select(i => new ArchiveEntry(i, prefix + (i / 2).ToString("D3"), false, 2, false)).ToArray();
        var model = new ArchivePresentation(new("ZIP", entries));
        var pages = new List<BrowserPage>();
        for (int start = 0; start < entries.Length;)
        {
            var page = model.Query(Query(start));
            Assert.Equal(start, page.Offset);
            Assert.NotEmpty(page.Rows);
            pages.Add(page);
            start += page.Rows.Length;
        }
        Assert.True(pages.Count > 4);
        Assert.Equal(entries.Select(e => e.Id.ToString()), pages.SelectMany(p => p.Rows.Select(r => r.Id)));
        for (int i = pages.Count - 1; i > 0; i--)
        {
            var previous = model.Query(Query(pages[i].Offset), previousPage: true);
            Assert.Equal(pages[i - 1].Rows, previous.Rows);
            Assert.Equal(pages[i].Rows, model.Query(Query(), revealId: long.Parse(pages[i].Rows[^1].Id)).Rows);
        }
    }
    [Fact]
    public void DeepImplicitPathsAreRejectedBeforeIndexExpansion()
    {
        var path=string.Concat(Enumerable.Repeat("a/",16000))+"x";
        var error=Assert.Throws<ArchiveOperationException>(()=>new ArchivePresentation(new("ZIP",[new(1,path,false,1,false)])));
        Assert.Equal("ResourceLimit",error.Code);
    }
}
