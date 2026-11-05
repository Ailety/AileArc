using AileArc.Core;
using AileArc.Core.Compression;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Theory]
    [InlineData("ZIP", "Fast", false)]
    [InlineData("ZIP", "Balanced", false)]
    [InlineData("ZIP", "Maximum", true)]
    [InlineData("7Z", "Fast", false)]
    [InlineData("7Z", "Balanced", true)]
    [InlineData("7Z", "Maximum", false)]
    public async Task CreatedArchivesRoundTripUnicodeEmptyFoldersAndPassword(string format, string preset, bool encrypted)
    {
        string source = Path.Combine(temp, "Project");
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        string contents = string.Concat(Enumerable.Repeat("AileArc 压缩测试。", 300));
        await File.WriteAllTextAsync(Path.Combine(source, "中文.txt"), contents);
        await File.WriteAllTextAsync(Path.Combine(source, "other.txt"), "second file");
        string destination = Path.Combine(temp, "created." + format.ToLowerInvariant());
        string? password = encrypted ? "fixture-password" : null;
        var phases = new HashSet<string>();
        var result = await client.CreateAsync(new([source], destination, format, preset, password, encrypted && format == "7Z"),
            new InlineProgress<CompressionProgress>(p => phases.Add(p.Phase)));
        Assert.Contains("verifying", phases);
        Assert.Equal(4, result.Entries);
        var scan = await client.ScanAsync(destination, password: password);
        Assert.Equal(format, scan.Format);
        Assert.Contains(scan.Entries, e => e.IsDirectory && e.Path.EndsWith("空目录"));
        var extracted = await client.ExtractAsync(destination, new(Path.Combine(temp, "output"), Smart: false), password: password);
        Assert.Empty(extracted.Failures);
        Assert.Equal(contents, await File.ReadAllTextAsync(Path.Combine(extracted.Destination, "Project", "中文.txt")));
        Assert.Empty(Directory.GetFiles(temp, "*.partial", SearchOption.AllDirectories));
        if (encrypted)
        {
            if (format == "7Z")
            {
                var failure = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(destination, new(Path.Combine(temp, "wrong-password")), password: "wrong"));
                Assert.Equal("WrongPasswordOrDamaged", failure.Code);
            }
            else
            {
                var failures = (await client.ExtractAsync(destination, new(Path.Combine(temp, "wrong-password")), password: "wrong")).Failures;
                Assert.NotEmpty(failures);
                Assert.All(failures, f =>
                {
                    Assert.NotNull(f.EntryId);
                    Assert.Equal("WrongPasswordOrDamaged", f.Code);
                });
            }
        }
    }

    [Fact]
    public async Task CreateCollapsesOverlappingSourcesButIncludesDistinctSiblingPrefixes()
    {
        string parent = Directory.CreateDirectory(Path.Combine(temp, "Project")).FullName;
        string child = Path.Combine(parent, "子项.txt");
        string sibling = Path.Combine(temp, "Project2.txt");
        await File.WriteAllTextAsync(child, "first");
        await File.WriteAllTextAsync(sibling, "second");
        string zip = Path.Combine(temp, "overlap.zip");
        await client.CreateAsync(new([child, sibling, parent, child.ToUpperInvariant()], zip));
        var scan = await client.ScanAsync(zip);
        Assert.Equal(new[] { "Project/子项.txt", "Project2.txt" }, scan.Entries.Where(e => !e.IsDirectory)
            .Select(e => e.Path.Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreationDistinguishesSourceCountAndByteBudgetsBeforeWriting()
    {
        string output = Path.Combine(temp, "existing.zip");
        await File.WriteAllTextAsync(output, "keep");
        var many = Enumerable.Range(0, 10_001).Select(i => Path.Combine(temp, $"source-{i}")).ToArray();
        var count = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new(many, output, Overwrite: true)));
        Assert.Equal("CompressionManifestLimit", count.Code);
        string one = Path.Combine(temp, "one.txt");
        await File.WriteAllTextAsync(one, "sample");
        var bytes = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([one], output, Overwrite: true, ByteLimit: 1)));
        Assert.Equal("CompressionByteLimit", bytes.Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }

    [Fact]
    public async Task CreateRefusesExistingDestinationUnlessExplicitlyRequested()
    {
        string source = Path.Combine(temp, "a.txt");
        string destination = Path.Combine(temp, "old.zip");
        await File.WriteAllTextAsync(source, "test");
        await File.WriteAllTextAsync(destination, "existing archive");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], destination)));
        Assert.Equal("DestinationConflict", error.Code);
        Assert.Equal("existing archive", await File.ReadAllTextAsync(destination));
        await client.CreateAsync(new([source], destination, Overwrite: true));
        Assert.Single((await client.ScanAsync(destination)).Entries);
    }

    [Fact]
    public async Task CancelledCreationKeepsExistingArchiveAndRemovesStagingFile()
    {
        string source = Path.Combine(temp, "a.txt");
        string destination = Path.Combine(temp, "old.zip");
        await File.WriteAllTextAsync(source, "test");
        await File.WriteAllTextAsync(destination, "keep original");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateAsync(new([source], destination, Overwrite: true),
            new InlineProgress<CompressionProgress>(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Equal("keep original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(temp, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CreationRejectsDestinationInsideSourceAndLinks()
    {
        string source = Path.Combine(temp, "source");
        Directory.CreateDirectory(source);
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], Path.Combine(source, "self.zip"))));
        Assert.Equal("DestinationInsideSource", error.Code);
        string outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(source, "link");
        JunctionFixture.Create(link, outside);
        try
        {
            error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], Path.Combine(temp, "links.zip"))));
            Assert.Equal("LinksNotSupported", error.Code);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task OverlappingSelectionsAreDeduplicated()
    {
        string source = Path.Combine(temp, "folder");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(file, "content");
        string destination = Path.Combine(temp, "deduplicated.zip");
        await client.CreateAsync(new([source, file, source], destination));
        Assert.Equal(2, (await client.ScanAsync(destination)).Entries.Count);
    }

    [Fact]
    public async Task SourceChangedBeforeNativeReadDoesNotReplaceExistingArchive()
    {
        string source = Path.Combine(temp, "a.txt");
        string destination = Path.Combine(temp, "old.zip");
        await File.WriteAllTextAsync(source, "before");
        await File.WriteAllTextAsync(destination, "keep original");
        bool changed = false;
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], destination, Overwrite: true),
            new InlineProgress<CompressionProgress>(_ => { if (!changed) { File.WriteAllText(source, "changed input length"); changed = true; } })));
        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal("keep original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(temp, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationAtVerificationKeepsOriginalDestination()
    {
        string source = Path.Combine(temp, "a.txt");
        string destination = Path.Combine(temp, "old.7z");
        await File.WriteAllTextAsync(source, "source data");
        await File.WriteAllTextAsync(destination, "keep original");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateAsync(new([source], destination, "7Z", Overwrite: true),
            new InlineProgress<CompressionProgress>(p => { if (p.Phase == "verifying") cancel.Cancel(); }), cancel.Token));
        Assert.Equal("keep original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(temp, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EmptyDirectoryCreatesAValidArchive()
    {
        string source = Path.Combine(temp, "empty");
        Directory.CreateDirectory(source);
        string destination = Path.Combine(temp, "empty.zip");
        await client.CreateAsync(new([source], destination));
        Assert.True(Assert.Single((await client.ScanAsync(destination)).Entries).IsDirectory);
    }
}
