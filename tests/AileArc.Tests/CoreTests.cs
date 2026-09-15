using System.Buffers.Binary;
using System.Text.RegularExpressions;
using AileArc.Core;
using AileArc.Shared;
using Xunit;

namespace AileArc.Tests;

public sealed class CoreTests
{
    private static ArchiveEntry File(long id, string path) => new(id, path, false, 10, false);

    [Fact]
    public void IndexPreservesDuplicatesAndCaseWhileSynthesizingParents()
    {
        var index = new ArchiveIndex([File(0, "src/code/a.cs"), File(1, "src/code/a.cs"), File(2, "src/code/A.cs")]);
        Assert.Equal("src", Assert.Single(index.Query("")).Name);
        Assert.Equal("code", Assert.Single(index.Query("src")).Name);
        Assert.Equal(3, index.Query("src/code").Length);
        Assert.Equal(3, index.Query("", "a.cs").Length);
        Assert.Empty(index.Query("", "a.cs", currentDirectoryOnly: true));
    }

    [Fact]
    public void ExplicitDirectoryDoesNotDuplicateSynthesizedNavigationFolder()
    {
        var index = new ArchiveIndex([File(0, "src/a.cs"), new(1, "src/", true, null, false)]);
        Assert.Single(index.Query(""));
        Assert.Single(index.Query("src"));
    }

    [Fact]
    public void SortAlwaysKeepsFoldersFirst()
    {
        var index = new ArchiveIndex([File(0, "z.txt"), File(1, "a/file.txt")]);
        Assert.True(index.Query("", descending: true)[0].IsDirectory);
    }

    [Theory]
    [InlineData("Project/file.txt", SmartExtractionKind.SingleDirectory, "Project")]
    [InlineData("report.pdf", SmartExtractionKind.SingleFile, "report.pdf")]
    public void SmartExtractChoosesSingleRoot(string path, SmartExtractionKind expected, string name)
    {
        var plan = SmartExtractionPlan.Create([File(0, path)]);
        Assert.Equal(expected, plan.Kind);
        Assert.Equal(name, plan.RootName);
    }

    [Fact]
    public void ReadmeBesideFolderRequiresContainer()
    {
        var plan = SmartExtractionPlan.Create([File(0, "Project/main.cs"), File(1, "安装说明.txt")]);
        Assert.Equal(SmartExtractionKind.Container, plan.Kind);
    }

    [Fact]
    public void DuplicateRootFilesCannotBeTreatedAsOneFile()
    {
        Assert.Equal(SmartExtractionKind.Container, SmartExtractionPlan.Create([File(0, "a.txt"), File(1, "a.txt")]).Kind);
        Assert.Equal(SmartExtractionKind.Empty, SmartExtractionPlan.Create([]).Kind);
    }

    [Fact]
    public void LanguagesHaveMatchingKeysAndPlaceholders()
    {
        var chinese = LanguageService.Load("zh-CN");
        var english = LanguageService.Load("en-US");
        Assert.Equal(chinese.Keys.Order(), english.Keys.Order());
        foreach (string key in chinese.Keys)
        {
            static string[] Tokens(string value) => Regex.Matches(value, @"\{\d+(?:[^}]*)\}").Select(m => m.Value).Order().ToArray();
            Assert.Equal(Tokens(chinese[key]), Tokens(english[key]));
        }
        Assert.Equal("打开压缩包", new LanguageService("invalid")["Open"]);
        Assert.Equal("Open archive", new LanguageService("en-US")["Open"]);
    }

    [Fact]
    public async Task ProtocolRoundTripsNewlinesAndUnicodePaths()
    {
        using var stream = new MemoryStream();
        await ArchiveProtocol.WriteAsync(stream, new ScanRequest(1, "C:\\中文\\line\nbreak.zip"));
        stream.Position = 0;
        var request = await ArchiveProtocol.ReadAsync<ScanRequest>(stream);
        Assert.Equal("C:\\中文\\line\nbreak.zip", request.Path);
    }

    [Fact]
    public async Task ProtocolRejectsOversizedFrameBeforeAllocatingBody()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveProtocol.ReadAsync<ScanRequest>(stream));
    }

    [Fact]
    public async Task ProtocolRejectsTruncatedFrame()
    {
        using var stream = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => ArchiveProtocol.ReadAsync<ScanRequest>(stream));
    }
}
