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
    public async Task SameLengthReplacementSourceWithRestoredTimeCannotSupplyArchiveBytes()
    {
        string source = Path.Combine(temp, "source.txt"), destination = Path.Combine(temp, "keep.zip");
        await File.WriteAllTextAsync(source, "aaaa"); await File.WriteAllTextAsync(destination, "keep");
        var writeTime = File.GetLastWriteTimeUtc(source);
        bool changed = false;
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], destination, Overwrite: true),
            new InlineProgress<CompressionProgress>(_ =>
            {
                if (changed) return;
                string other = Path.Combine(temp, "other");
                File.WriteAllText(other, "bbbb"); File.SetLastWriteTimeUtc(other, writeTime);
                File.Move(other, source, true); changed = true;
            })));
        Assert.True(changed); Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(source, error.AffectedPath);
        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetAppearingOrChangingDuringVerificationIsPreserved(bool originallyExisted)
    {
        string source = Path.Combine(temp, "source.txt"), destination = Path.Combine(temp, "output.zip");
        await File.WriteAllTextAsync(source, "archive this");
        if (originallyExisted) await File.WriteAllTextAsync(destination, "original");
        bool changed = false;
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], destination, Overwrite: true),
            new InlineProgress<CompressionProgress>(p =>
            {
                if (changed || p.Phase != "verifying") return;
                string other = Path.Combine(temp, "other");
                File.WriteAllText(other, "external"); File.Move(other, destination, true); changed = true;
            })));
        Assert.True(changed); Assert.Equal("CompressionTargetChanged", error.Code);
        Assert.Equal("external", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }

    [Fact]
    public async Task ChangeDuringVerificationStopsPublicationEvenAfterWorkerReadTheSources()
    {
        string source = Path.Combine(temp, "empty.txt"), destination = Path.Combine(temp, "keep.zip");
        await File.WriteAllTextAsync(source, ""); await File.WriteAllTextAsync(destination, "keep");
        bool changed = false;
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], destination, Overwrite: true),
            new InlineProgress<CompressionProgress>(p =>
            {
                if (changed || p.Phase != "verifying") return;
                File.WriteAllText(source, "new external contents"); changed = true;
            })));
        Assert.True(changed); Assert.Equal("SourceChanged", error.Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
        Assert.Equal("new external contents", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }

    [Fact]
    public async Task TwoCreationsConfirmedAgainstTheSameFileCannotBothReplaceIt()
    {
        string target = Path.Combine(temp, "shared.zip");
        await File.WriteAllTextAsync(target, "original");
        var agreed = CompressionDestination.Capture(target);
        using var barrier = new Barrier(2);
        async Task<bool> AttemptAsync(string name)
        {
            string source = Path.Combine(temp, name);
            await File.WriteAllTextAsync(source, name);
            bool met = false;
            try
            {
                await client.CreateAsync(new([source], target, Overwrite: true, ConfirmedDestination: agreed),
                    new InlineProgress<CompressionProgress>(p =>
                    {
                        if (met || p.Phase != "verifying") return;
                        met = true;
                        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
                    }));
                return true;
            }
            catch (ArchiveOperationException error) when (error.Code == "CompressionTargetChanged") { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => AttemptAsync("one.txt")), Task.Run(() => AttemptAsync("two.txt")));
        Assert.Equal(1, outcomes.Count(success => success));
        Assert.Equal(outcomes[0] ? "one.txt" : "two.txt", Assert.Single((await client.ScanAsync(target)).Entries).Path);
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }

    [Fact]
    public async Task StagingFileIsExclusiveUntilItIsPublished()
    {
        string source = Path.Combine(temp, "source.txt"), target = Path.Combine(temp, "archive.zip");
        await File.WriteAllTextAsync(source, "original input");
        bool checkedStage = false;
        var result = await client.CreateAsync(new([source], target), new InlineProgress<CompressionProgress>(p =>
        {
            if (p.Phase != "verifying" || checkedStage) return;
            string stage = Assert.Single(Directory.GetFiles(temp, "*.partial"));
            Assert.Throws<IOException>(() => { using var file = File.OpenWrite(stage); });
            Assert.Throws<IOException>(() => File.Move(stage, stage + ".moved"));
            checkedStage = true;
        }));
        Assert.True(checkedStage);
        Assert.Equal(1, result.Entries);
        Assert.Empty((await client.TestAsync(target, await client.ScanAsync(target))).Failures);
    }

    [Fact]
    public async Task DirectoryMembershipChangeDuringVerificationDoesNotSilentlyDropNewFiles()
    {
        string folder = Directory.CreateDirectory(Path.Combine(temp, "selected")).FullName, target = Path.Combine(temp, "out.zip");
        await File.WriteAllTextAsync(Path.Combine(folder, "first.txt"), "one");
        bool added = false;
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([folder], target),
            new InlineProgress<CompressionProgress>(p =>
            {
                if (added || p.Phase != "verifying") return;
                File.WriteAllText(Path.Combine(folder, "second.txt"), "two"); added = true;
            })));
        Assert.True(added); Assert.Equal("SourceChanged", error.Code);
        Assert.False(File.Exists(target));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Theory]
    [InlineData("changed-stage", "CompressionOutputChanged")]
    [InlineData("unverified-stage", "ProtocolMismatch")]
    public async Task OnlyTheExactVerifiedStagingVersionCanBePublished(string fixture, string expectedError)
    {
        string source = Path.Combine(temp, fixture), target = Path.Combine(temp, "out.zip");
        await File.WriteAllTextAsync(source, "input"); await File.WriteAllTextAsync(target, "original");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => SlowWorker().CreateAsync(new([source], target, Overwrite: true)));
        Assert.Equal(expectedError, error.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }

    [Fact]
    public async Task DestinationCaptureRejectsJunctionBeforeInspectingTheOutsideFile()
    {
        string outside = Directory.CreateDirectory(Path.Combine(temp, "outside")).FullName;
        string original = Path.Combine(outside, "file.zip"), link = Path.Combine(temp, "redirect");
        await File.WriteAllTextAsync(original, "keep");
        JunctionFixture.Create(link, outside);
        try
        {
            var error = Assert.Throws<ArchiveOperationException>(() => CompressionDestination.Capture(Path.Combine(link, "file.zip")));
            Assert.Equal("UnsafePath", error.Code);
            Assert.Equal("keep", await File.ReadAllTextAsync(original));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task DestinationAliasOfSelectedSourceIsPreserved()
    {
        string source = Path.Combine(temp, "source.txt"), target = Path.Combine(temp, "alias.zip");
        await File.WriteAllTextAsync(source, "keep both names");
        Assert.True(CreateCopyTestLink(target, source, IntPtr.Zero));
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([source], target, Overwrite: true)));
        Assert.Equal("DestinationInsideSource", error.Code);
        Assert.Equal("keep both names", await File.ReadAllTextAsync(target));
        Assert.Equal("keep both names", await File.ReadAllTextAsync(source));
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
    public async Task DisappearedInputIsIdentifiedWithoutCreatingTheOutputDirectory()
    {
        string missing = Path.Combine(temp, "removed.txt"), folder = Path.Combine(temp, "output");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.CreateAsync(new([missing], Path.Combine(folder, "new.zip"))));
        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(missing, error.AffectedPath);
        Assert.False(Directory.Exists(folder));
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
