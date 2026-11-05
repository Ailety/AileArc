using System.IO.Compression;
using AileArc.Core;
using AileArc.Core.Extraction;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Fact]
    public async Task TestReportsCorruptionByIdWithoutCreatingOrChangingAnyFile()
    {
        string path = Path.Combine(temp, "damaged.zip");
        byte[] payload = "corrupt-this-entry"u8.ToArray();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var a = zip.CreateEntry("same", CompressionLevel.NoCompression).Open()) a.Write(payload);
            using (var b = zip.CreateEntry("same").Open()) b.WriteByte(17);
            zip.CreateEntry("empty-dir/");
        }
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.AsSpan().IndexOf(payload)] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        var scan = await client.ScanAsync(path);
        var result = await client.TestAsync(path, scan);
        Assert.Equal(3, result.Checked);
        var issue = Assert.Single(result.Failures);
        Assert.Equal(0, issue.EntryId);
        Assert.Equal("IntegrityFailed", issue.Code);
        Assert.Equal(path, Assert.Single(Directory.GetFileSystemEntries(temp)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task TestRespectsPasswordsIdentityAndDecodedSize()
    {
        string input = Path.Combine(temp, "secret.txt"), path = Path.Combine(temp, "encrypted.zip");
        await File.WriteAllTextAsync(input, "fixture data");
        await client.CreateAsync(new([input], path, Password: "fixture-pass"));
        var scan = await client.ScanAsync(path);
        Assert.Equal("PasswordRequired", (await Assert.ThrowsAsync<ArchiveOperationException>(() => client.TestAsync(path, scan))).Code);
        Assert.Equal("WrongPasswordOrDamaged", Assert.Single((await client.TestAsync(path, scan, password: "wrong")).Failures).Code);
        Assert.Empty((await client.TestAsync(path, scan, password: "fixture-pass")).Failures);
        var oversized = scan with { Entries = [scan.Entries[0] with { Size = ulong.MaxValue }] };
        Assert.Equal("TestByteLimit", (await Assert.ThrowsAsync<ArchiveOperationException>(() => client.TestAsync(path, oversized))).Code);
        File.Move(MakeZip("changed.txt"), path, overwrite: true);
        Assert.Equal("ArchiveChanged", (await Assert.ThrowsAsync<ArchiveOperationException>(() => client.TestAsync(path, scan))).Code);
    }

    [Fact]
    public async Task UnsupportedCompressionIsReportedSeparatelyFromAChecksumFailure()
    {
        string path = MakeZip("sample.txt");
        byte[] data = await File.ReadAllBytesAsync(path);
        int directory = data.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(directory > 0);
        // Local header and central directory must agree on the unknown ZIP method.
        data[8] = data[directory + 10] = 0xfd;
        data[9] = data[directory + 11] = 0x7f;
        await File.WriteAllBytesAsync(path, data);
        var scan = await client.ScanAsync(path);
        Assert.Equal("EntryMethodUnsupported", Assert.Single((await client.TestAsync(path, scan)).Failures).Code);
        var output = Path.Combine(temp, "out");
        Assert.Equal("EntryMethodUnsupported", Assert.Single((await client.ExtractAsync(path, new(output, Smart: false))).Failures).Code);
        Assert.Empty(Directory.GetFiles(output));
    }

    [Fact]
    public async Task EmptyArchiveTestHasNoOutput()
    {
        string path = MakeZip();
        var result = await client.TestAsync(path, await client.ScanAsync(path));
        Assert.Equal(0, result.Checked);
        Assert.Empty(result.Failures);
        Assert.Equal(path, Assert.Single(Directory.GetFileSystemEntries(temp)));
    }

    [Theory]
    [InlineData("missing-check")]
    [InlineData("duplicate-check")]
    [InlineData("test-sends-data")]
    public async Task TestRequiresOneResultPerIdAndRejectsPayloadFrames(string mode)
    {
        var fake = SlowWorker();
        var snapshot = new ArchiveScan("ZIP", [new(0, "sample.txt", false, 1, false)], new("fixture", 0, 0));
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => fake.TestAsync(Path.Combine(temp, mode), snapshot));
        Assert.Equal("ProtocolMismatch", error.Code);
    }

    [Fact]
    public async Task CancellationDuringTestAllowsAnotherTestOfTheSameArchive()
    {
        string path = MakeZip(Enumerable.Range(0, 300).Select(i => $"file-{i}").ToArray());
        var scan = await client.ScanAsync(path);
        using var cancelled = new CancellationTokenSource();
        var progress = new InlineProgress<ArchiveTestProgress>(p => { if (p.Checked > 0) cancelled.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TestAsync(path, scan, progress, cancelled.Token));
        var retry = await client.TestAsync(path, scan);
        Assert.Equal(300, retry.Checked);
        Assert.Empty(retry.Failures);
    }
}
