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
    public void DeepImplicitPathsAreRejectedBeforeIndexExpansion()
    {
        var path=string.Concat(Enumerable.Repeat("a/",16000))+"x";
        var error=Assert.Throws<ArchiveOperationException>(()=>new ArchivePresentation(new("ZIP",[new(1,path,false,1,false)])));
        Assert.Equal("ResourceLimit",error.Code);
    }
}
