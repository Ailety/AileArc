using System.IO.Compression;
using System.Text;
using AileArc.Core;
using AileArc.Core.Extraction;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Fact]
    public async Task ExtractsVerifiedContentAndPreservesStructure()
    {
        string archive = MakeZip("中文/说明.txt", "README.md");
        var result = await client.ExtractAsync(archive, new(Path.Combine(temp, "output"), Smart: false));
        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Completed);
        Assert.Equal("AileArc 测试内容", await File.ReadAllTextAsync(Path.Combine(temp, "output", "中文", "说明.txt")));
        Assert.Empty(Directory.GetFiles(temp, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SmartExtractReservesUniqueRootWithoutMerging()
    {
        string destination = Path.Combine(temp, "output");
        Directory.CreateDirectory(Path.Combine(destination, "Project"));
        await File.WriteAllTextAsync(Path.Combine(destination, "Project", "old.txt"), "keep");
        var result = await client.ExtractAsync(MakeZip("Project/new.txt"), new(destination));
        Assert.Empty(result.Failures);
        Assert.Equal(Path.Combine(destination, "Project (2)"), result.Destination);
        Assert.True(File.Exists(Path.Combine(result.Destination, "new.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "Project", "new.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "Project", "old.txt")));
    }

    [Fact]
    public async Task SmartSingleFileRenamesAndDoesNotWrap()
    {
        string destination = Path.Combine(temp, "output");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "report.pdf"), "keep");
        var result = await client.ExtractAsync(MakeZip("report.pdf"), new(destination));
        Assert.Empty(result.Failures);
        Assert.True(File.Exists(Path.Combine(destination, "report (2).pdf")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "report.pdf")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("file.txt:payload")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/trailing. ")]
    public async Task UnsafeArchiveIsRejectedBeforeAnyOutput(string entry)
    {
        string destination = Path.Combine(temp, "output");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(MakeZip("safe.txt", entry), new(destination)));
        Assert.Equal("UnsafePath", error.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(ConflictChoice.Skip, "original", 0, 1)]
    [InlineData(ConflictChoice.Replace, "AileArc 测试内容", 1, 0)]
    [InlineData(ConflictChoice.Rename, "original", 1, 0)]
    public async Task ConflictChoicesPreserveExpectedContent(ConflictChoice choice, string expected, int completed, int skipped)
    {
        string destination = Path.Combine(temp, "output");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "a.txt"), "original");
        int prompts = 0;
        var result = await client.ExtractAsync(MakeZip("a.txt"), new(destination, Smart: false),
            (_, _) => { prompts++; return Task.FromResult(new ConflictDecision(choice)); });
        Assert.Empty(result.Failures);
        Assert.Equal(1, prompts);
        Assert.Equal(completed, result.Completed);
        Assert.Equal(skipped, result.Skipped);
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public async Task CancellationPreservesCompletedFilesAndCleansPartialFile()
    {
        string destination = Path.Combine(temp, "output");
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress<ExtractionProgress>(p => { if (p.Completed >= 1) cancel.Cancel(); });
        var result = await client.ExtractAsync(MakeZip("a.txt", "b.txt"), new(destination, Smart: false), progress: progress, cancellationToken: cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Completed);
        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "b.txt")));
        Assert.Empty(Directory.GetFiles(destination, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SizeBudgetRejectsBeforeCreatingDestination()
    {
        string destination = Path.Combine(temp, "output");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(MakeZip("a.txt"), new(destination, ByteLimit: 1)));
        Assert.Equal("ResourceLimit", error.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task CrcFailureNeverReplacesExistingFile()
    {
        string archive = Path.Combine(temp, "corrupt.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("a.txt", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
            writer.Write("original-content-for-crc");
        byte[] bytes = await File.ReadAllBytesAsync(archive);
        // Locate bytes independently because binary headers do not have a 1:1 UTF-8 character mapping.
        var needle = Encoding.ASCII.GetBytes("original-content-for-crc");
        int index = bytes.AsSpan().IndexOf(needle);
        Assert.True(index >= 0);
        bytes[index] ^= 1;
        await File.WriteAllBytesAsync(archive, bytes);
        string destination = Path.Combine(temp, "output");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "a.txt"), "keep me");
        var result = await client.ExtractAsync(archive, new(destination, Smart: false, ConflictPolicy: ConflictChoice.Replace));
        Assert.Single(result.Failures);
        Assert.Equal(0, result.Completed);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Empty(Directory.GetFiles(destination, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadZoneIsPropagatedToExtractedFile()
    {
        string archive = MakeZip("a.txt");
        await File.WriteAllTextAsync(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.test/private\r\n");
        string destination = Path.Combine(temp, "output");
        var result = await client.ExtractAsync(archive, new(destination, Smart: false));
        Assert.Empty(result.Failures);
        string zone = await File.ReadAllTextAsync(Path.Combine(destination, "a.txt") + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone);
        Assert.DoesNotContain("HostUrl", zone);
    }

    [Fact]
    public async Task EmptyArchiveDoesNotCreateDestination()
    {
        string destination = Path.Combine(temp, "output");
        var result = await client.ExtractAsync(MakeZip(), new(destination));
        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RarFixtureListsAndExtractsSelectedRegularFile()
    {
        // libarchive/test/test_read_format_rar.rar.uu, BSD-2-Clause; see THIRD_PARTY_NOTICES.md.
        const string fixture = "UmFyIRoHAM+QcwAADQAAAAAAAACEUnQgkDIAFAAAABQAAAADQqLIvrd22j4UMAgApIEAAHRlc3QudHh0gAi3dto+t3baPnRlc3QgdGV4dCBkb2N1bWVudA0KnS90IJAyAAgAAAAIAAAAA3tEybbRTNg+FDAIAP+hAAB0ZXN0bGlua8AI0UzYPlBf2j50ZXN0LnR4dM3gdCCQOgAUAAAAFAAAAANCosi+Y3faPhQwEACkgQAAdGVzdGRpclx0ZXN0LnR4dMDMY3faPmN32j50ZXN0IHRleHQgZG9jdW1lbnQNCqHIdOCQMQAAAAAAAAAAAAMAAAAAY3faPhQwBwDtQQAAdGVzdGRpcsDMY3faPmR32j7m53TgkDYAAAAAAAAAAAADAAAAAJ2r1T4UMAwA7UEAAHRlc3RlbXB0eWRpcoDMnavVPsVd2j7EPXsAQAcA";
        string archive = Path.Combine(temp, "sample.rar");
        await File.WriteAllBytesAsync(archive, Convert.FromBase64String(fixture));
        var scan = await client.ScanAsync(archive);
        Assert.Equal("RAR", scan.Format);
        Assert.Equal(5, scan.Entries.Count);
        Assert.Contains(scan.Entries, e => e.IsLink);
        var file = Assert.Single(scan.Entries, e => e.Path == "test.txt");
        var result = await client.ExtractAsync(archive, new(Path.Combine(temp, "rar-output"), Smart: false, SelectedIds: [file.Id]));
        Assert.Empty(result.Failures);
        Assert.Equal("test text document\r\n", await File.ReadAllTextAsync(Path.Combine(result.Destination, "test.txt")));
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(archive, new(Path.Combine(temp, "rar-all"))));
        Assert.Equal("LinksNotSupported", failure.Code);
    }

    [Fact]
    public async Task ExistingDestinationJunctionCannotEscapeOutput()
    {
        string destination = Path.Combine(temp, "output");
        string outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(destination, "Project");
        JunctionFixture.Create(link, outside);
        try
        {
            var result = await client.ExtractAsync(MakeZip("Project/escape.txt"), new(destination, Smart: false));
            Assert.Equal("UnsafePath", Assert.Single(result.Failures).Code);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task DestinationRootJunctionIsRejected()
    {
        string outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(temp, "output");
        JunctionFixture.Create(link, outside);
        try
        {
            var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(MakeZip("escape.txt"), new(link)));
            Assert.Equal("UnsafePath", error.Code);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CaseCollisionsAndDuplicatePathsGetDistinctFiles()
    {
        string destination = Path.Combine(temp, "output");
        var result = await client.ExtractAsync(MakeZip("A.txt", "a.txt", "A.txt"), new(destination, Smart: false, ConflictPolicy: ConflictChoice.Rename));
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Completed);
        Assert.Equal(3, Directory.GetFiles(destination).Length);
    }

    [Fact]
    public async Task CancellationMidFileRemovesOnlyOwnedPartial()
    {
        string destination = Path.Combine(temp, "output");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "original");
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress<ExtractionProgress>(p => { if (p.Bytes > 0) cancel.Cancel(); });
        var result = await client.ExtractAsync(MakeZip("a.txt"), new(destination, Smart: false), progress: progress, cancellationToken: cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Completed);
        Assert.Equal("keep.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(destination))));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
    }

    [Fact]
    public async Task DifferentlyCasedDirectoriesAreNotSilentlyMerged()
    {
        string destination = Path.Combine(temp, "output");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(MakeZip("Project/a.txt", "project/b.txt"), new(destination)));
        Assert.Equal("DirectoryCaseConflict", error.Code);
        Assert.False(Directory.Exists(destination));
    }
}
