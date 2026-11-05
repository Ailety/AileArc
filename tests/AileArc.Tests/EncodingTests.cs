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
    public async Task ReplacedEncryptedArchiveIsRejectedBeforeAnyPasswordOrEncodingAttempt()
    {
        string archive = MakeZip("old.txt");
        var snapshot = await client.ScanAsync(archive);
        string file = Path.Combine(temp, "new.txt"), replacement = Path.Combine(temp, "secret.7z");
        await File.WriteAllTextAsync(file, "new");
        await client.CreateAsync(new([file], replacement, "7Z", Password: "fixture", EncryptNames: true));
        File.Move(replacement, archive, overwrite: true);
        var scan = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(archive, password: "prior-session",
            nameCodePage: 936, expectedIdentity: snapshot.Identity));
        Assert.Equal("ArchiveChanged", scan.Code);
        string destination = Path.Combine(temp, "should-not-exist");
        var extract = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(archive, new(destination, ExpectedIdentity: snapshot.Identity)));
        Assert.Equal("ArchiveChanged", extract.Code);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("ArchiveChanged", (await Assert.ThrowsAsync<ArchiveOperationException>(() => client.TestAsync(archive, snapshot))).Code);
        Assert.Equal("7Z", (await client.ScanAsync(archive, password: "fixture")).Format);
    }

    [Fact]
    public async Task SameLengthRewriteWithRestoredTimestampInvalidatesNtfsSnapshot()
    {
        string archive = MakeZip("a.txt");
        var previous = await client.ScanAsync(archive);
        var time = File.GetLastWriteTimeUtc(archive);
        byte[] replacement = await File.ReadAllBytesAsync(MakeZip("b.txt"));
        Assert.Equal(previous.Identity!.Length, replacement.Length);
        await File.WriteAllBytesAsync(archive, replacement);
        File.SetLastWriteTimeUtc(archive, time);
        var current = await client.ScanAsync(archive);
        Assert.Equal(previous.Identity.FileId, current.Identity!.FileId);
        Assert.Equal(previous.Identity.LastWriteTime, current.Identity.LastWriteTime);
        if (previous.Identity.ChangeTime.HasValue)
        {
            Assert.NotEqual(previous.Identity.ChangeTime, current.Identity.ChangeTime);
            var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(archive, expectedIdentity: previous.Identity));
            Assert.Equal("ArchiveChanged", error.Code);
        }
        Assert.Equal("b.txt", Assert.Single(current.Entries).Path);
    }

    [Fact]
    public async Task ArbitraryCodePagesAreNotPassedToNativeEngine()
    {
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(MakeZip("a.txt"), nameCodePage: -1));
        Assert.Equal("EncodingNotSupported", error.Code);
    }
}
