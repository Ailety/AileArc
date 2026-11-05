using System.IO.Compression;
using AileArc.Core;
using AileArc.Core.Extraction;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    private static async Task WithPrefixAsync(string source, string target, int length)
    {
        await using var output = File.Create(target);
        output.Write("MZ"u8);
        output.Position = length;
        await using var input = File.OpenRead(source);
        await input.CopyToAsync(output);
    }

    [Theory]
    [InlineData("ZIP")]
    [InlineData("7Z")]
    public async Task PrefixedArchiveWithWrongExtensionCanBeReadAndExtracted(string format)
    {
        string file = Path.Combine(temp, "设置.yml");
        await File.WriteAllTextAsync(file, "测试数据");
        string source = Path.Combine(temp, "source." + format.ToLowerInvariant());
        await client.CreateAsync(new([file], source, format));
        string prefixed = Path.Combine(temp, "sample.exe");
        await WithPrefixAsync(source, prefixed, 65536);
        var scan = await client.ScanAsync(prefixed);
        Assert.Equal(format, scan.Format);
        Assert.Equal("设置.yml", Assert.Single(scan.Entries).Path);
        string output = Path.Combine(temp, "out");
        var extract = await client.ExtractAsync(prefixed, new(output, Smart: false, ExpectedIdentity: scan.Identity));
        Assert.Empty(extract.Failures);
        Assert.Equal("测试数据", await File.ReadAllTextAsync(Path.Combine(output, "设置.yml")));
    }

    [Fact]
    public async Task PrefixedZipAlsoAcceptsAbsoluteDirectoryOffsets()
    {
        string path = Path.Combine(temp, "absolute.bin");
        using (var output = File.Create(path))
        {
            output.Write("MZ"u8);
            output.Position = 8192;
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            using var file = zip.CreateEntry("ok.txt").Open();
            file.WriteByte(42);
        }
        var scan = await client.ScanAsync(path);
        Assert.Equal("ok.txt", Assert.Single(scan.Entries).Path);
        string destination = Path.Combine(temp, "absolute-out");
        Assert.Empty((await client.ExtractAsync(path, new(destination, Smart: false, ExpectedIdentity: scan.Identity))).Failures);
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(Path.Combine(destination, "ok.txt")));
    }

    [Fact]
    public async Task HeaderSearchStopsAtBudgetAndNeverRecoversANestedArchiveFromABrokenHeader()
    {
        string source = MakeZip("inside.txt");
        string outside = Path.Combine(temp, "outside.bin");
        await WithPrefixAsync(source, outside, 4 * 1024 * 1024 + 16);
        Assert.Equal("UnsupportedOrDamaged", (await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(outside))).Code);
        string broken = Path.Combine(temp, "broken.7z");
        await WithPrefixAsync(source, broken, 8192);
        using (var file = File.OpenWrite(broken)) file.Write(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0xff });
        Assert.Equal("UnsupportedOrDamaged", (await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(broken))).Code);
    }
}
