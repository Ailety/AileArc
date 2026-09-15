using System.IO.Compression;
using System.Text;
using AileArc.Core;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Theory]
    [InlineData(936, "中文说明.txt")]
    [InlineData(932, "日本語.txt")]
    [InlineData(65001, "编码测试.txt")]
    public async Task ExplicitZipFilenameEncodingIsUsedForBothBrowsingAndExtraction(int codePage, string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(temp, "legacy.zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.GetEncoding(codePage)))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("encoding fixture");
        var scan = await client.ScanAsync(path, nameCodePage: codePage);
        Assert.Equal(name, Assert.Single(scan.Entries).Path);
        Assert.Equal(codePage, scan.NameCodePage);
        var result = await client.ExtractAsync(path, new(Path.Combine(temp, "output"), Smart: false,
            ExpectedIdentity: scan.Identity, NameCodePage: codePage));
        Assert.Empty(result.Failures);
        Assert.Equal("encoding fixture", await File.ReadAllTextAsync(Path.Combine(result.Destination, name)));
    }

    [Fact]
    public async Task ArbitraryCodePagesAreNotPassedToNativeEngine()
    {
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(MakeZip("a.txt"), nameCodePage: -1));
        Assert.Equal("EncodingNotSupported", error.Code);
    }
}
