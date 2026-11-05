using AileArc.Core;
using AileArc.Core.Extraction;
using AileArc.Core.WorkCopies;
using AileArc.Shared;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    private async Task<(WorkCopyStore Store, WorkCopyRecord Record)> PrepareWorkCopyAsync(string entryName = "Project/config.yml")
    {
        string archive = MakeZip(entryName);
        var snapshot = await client.ScanAsync(archive);
        var entry = Assert.Single(snapshot.Entries);
        var store = new WorkCopyStore(Path.Combine(temp, "copies"));
        var record = await store.PrepareAsync(archive, snapshot.Identity!, entry,
            (destination, token) => client.ExtractAsync(archive, new(destination, Smart: false,
                SelectedIds: [entry.Id], ExpectedIdentity: snapshot.Identity), cancellationToken: token));
        return (store, record);
    }

    [Fact]
    public async Task WorkCopyDetectsEditsAndSurvivesNewStoreInstance()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        Assert.Equal(WorkCopyState.Unchanged, (await store.InspectAsync(record)).State);
        await File.WriteAllTextAsync(store.FilePath(record), "edited config");
        Assert.Equal(WorkCopyState.Modified, (await store.InspectAsync(record)).State);
        var restarted = new WorkCopyStore(store.Root);
        var recovered = Assert.Single(await restarted.LoadAsync());
        Assert.Equal(WorkCopyState.Modified, (await restarted.InspectAsync(recovered)).State);
        Assert.Equal("edited config", await File.ReadAllTextAsync(restarted.FilePath(recovered)));
    }

    [Fact]
    public async Task RepeatedPrepareNeverOverwritesEditedCopy()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        await File.WriteAllTextAsync(store.FilePath(record), "keep changes");
        var snapshot = await client.ScanAsync(record.ArchivePath);
        var again = await store.PrepareAsync(record.ArchivePath, snapshot.Identity!, Assert.Single(snapshot.Entries),
            (_, _) => throw new InvalidOperationException("Must not extract again"));
        Assert.Equal(record.Id, again.Id);
        Assert.Equal("keep changes", await File.ReadAllTextAsync(store.FilePath(again)));
    }

    [Fact]
    public async Task OlderWorkCopyManifestWithoutChangeTimeCanStillBeRecovered()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string manifest = Path.Combine(store.Root, record.Id, "workcopy.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
        json["Identity"]!.AsObject().Remove("ChangeTime");
        await File.WriteAllTextAsync(manifest, json.ToJsonString());
        var restarted = new WorkCopyStore(store.Root);
        var loaded = Assert.Single(await restarted.LoadAsync());
        Assert.Null(loaded.Identity.ChangeTime);
        Assert.Equal(WorkCopyState.Unchanged, (await restarted.InspectAsync(loaded)).State);
        string exported = Path.Combine(temp, "legacy-export.txt");
        await restarted.ExportAsync(loaded, exported, overwrite: false);
        Assert.Equal(await File.ReadAllTextAsync(store.FilePath(record)), await File.ReadAllTextAsync(exported));
    }

    [Fact]
    public async Task ChangedSourceCannotSilentlyReopenCachedCopyFromTheArchiveBrowser()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        var scan = await client.ScanAsync(record.ArchivePath);
        await File.WriteAllTextAsync(store.FilePath(record), "retained edits");
        File.Move(MakeZip("replacement.txt"), record.ArchivePath, overwrite: true);
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.PrepareAsync(record.ArchivePath, scan.Identity!,
            Assert.Single(scan.Entries), (_, _) => throw new InvalidOperationException("Stale entry must not be extracted")));
        Assert.Equal("ArchiveChanged", error.Code);
        Assert.Equal(WorkCopyState.Modified, (await store.InspectAsync(record)).State);
        Assert.Equal("retained edits", await File.ReadAllTextAsync(store.FilePath(record)));
    }

    [Fact]
    public async Task ExportDoesNotChangeArchiveOrResetModificationState()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        byte[] original = await File.ReadAllBytesAsync(record.ArchivePath);
        await File.WriteAllTextAsync(store.FilePath(record), "export these edits");
        string destination = Path.Combine(temp, "export", "config.yml");
        await store.ExportAsync(record, destination);
        Assert.Equal("export these edits", await File.ReadAllTextAsync(destination));
        Assert.Equal(original, await File.ReadAllBytesAsync(record.ArchivePath));
        Assert.Equal(WorkCopyState.Modified, (await store.InspectAsync(record)).State);
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.ExportAsync(record, record.ArchivePath));
        Assert.Equal("UnsafeExport", error.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(record.ArchivePath));
    }

    [Fact]
    public async Task LockedCopyIsRetainedAndExportDoesNotOverwriteDestination()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string destination = Path.Combine(temp, "existing.txt");
        await File.WriteAllTextAsync(destination, "keep original");
        using (var locked = new FileStream(store.FilePath(record), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(WorkCopyState.Unavailable, (await store.InspectAsync(record)).State);
            await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(record, destination));
        }
        Assert.Equal("keep original", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(store.FilePath(record)));
        Assert.Empty(Directory.GetFiles(temp, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChangedArchiveIsRejectedBeforeSelectedExtraction()
    {
        string archive = MakeZip("a.txt");
        var snapshot = await client.ScanAsync(archive);
        File.SetLastWriteTimeUtc(archive, File.GetLastWriteTimeUtc(archive).AddMinutes(1));
        string destination = Path.Combine(temp, "output");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(archive,
            new(destination, SelectedIds: [0], ExpectedIdentity: snapshot.Identity)));
        Assert.Equal("ArchiveChanged", error.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task SelectionIncludesDescendantsButNotSimilarPrefixOrDuplicateFiles()
    {
        string archive = MakeZip("Project/a.txt", "Project/a.txt", "Project/sub/b.txt", "Project2/c.txt");
        var snapshot = await client.ScanAsync(archive);
        var index = new ArchiveIndex(snapshot.Entries);
        var directory = Assert.Single(index.Query(""), e => e.Name == "Project");
        var selected = ArchiveSelection.Expand([directory], snapshot.Entries);
        Assert.Equal(new long[] { 0, 1, 2 }, selected);
        var file = Assert.Single(index.Query("Project"), e => e.Id == 1);
        Assert.Equal(new long[] { 1 }, ArchiveSelection.Expand([file], snapshot.Entries));
        Assert.Equal(selected, ArchiveSelection.Expand([directory, file], snapshot.Entries));
    }

    [Fact]
    public async Task CancelledPreparationIsRecoverableButNeverReadyToOpen()
    {
        string archive = MakeZip("a.txt");
        var snapshot = await client.ScanAsync(archive);
        var store = new WorkCopyStore(Path.Combine(temp, "copies"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareAsync(archive, snapshot.Identity!, snapshot.Entries[0],
            (destination, _) => Task.FromResult(new ExtractionResult(destination, 0, 0, [], true))));
        var incomplete = Assert.Single(await store.LoadAsync());
        Assert.Equal(WorkCopyState.Incomplete, (await store.InspectAsync(incomplete)).State);
    }

    [Theory]
    [InlineData("script.PS1", true)]
    [InlineData("app.exe", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("config.yml", false)]
    [InlineData("README.txt", false)]
    public void ExecutableOpeningRequiresConfirmation(string name, bool expected)
    { Assert.Equal(expected, ExternalOpenPolicy.RequiresConfirmation(name)); }

    [Fact]
    public async Task AtomicEditorSaveCannotRemoveOriginalDownloadMarkFromExport()
    {
        string archive = MakeZip("notes.txt");
        await File.WriteAllTextAsync(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var snapshot = await client.ScanAsync(archive);
        var store = new WorkCopyStore(Path.Combine(temp, "copies"));
        var record = await store.PrepareAsync(archive, snapshot.Identity!, snapshot.Entries[0],
            (destination, token) => client.ExtractAsync(archive, new(destination, Smart: false, SelectedIds: [0], ExpectedIdentity: snapshot.Identity), cancellationToken: token));
        Assert.Equal("3", record.SourceZone);
        string replacement = Path.Combine(temp, "edited.txt");
        await File.WriteAllTextAsync(replacement, "atomic editor save");
        File.Move(replacement, store.FilePath(record), overwrite: true);
        string exported = Path.Combine(temp, "exported.txt");
        await store.ExportAsync(record, exported);
        Assert.Contains("ZoneId=3", await File.ReadAllTextAsync(exported + ":Zone.Identifier"));
        await store.RestoreSourceMarkAsync(record);
        Assert.Contains("ZoneId=3", await File.ReadAllTextAsync(store.FilePath(record) + ":Zone.Identifier"));
    }

    [Fact]
    public async Task WorkCopyExportRejectsDirectoryJunctionAndPreservesOutsideFiles()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(temp, "link");
        JunctionFixture.Create(link, outside);
        try
        {
            var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.ExportAsync(record, Path.Combine(link, "file.txt")));
            Assert.Equal("UnsafePath", error.Code);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ExportRejectsRenamedOriginalAndRequiresConsentForAnOccupiedDestination()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string moved = Path.Combine(temp, "renamed.zip");
        File.Move(record.ArchivePath, moved);
        byte[] original = await File.ReadAllBytesAsync(moved);
        var unsafeTarget = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.ExportAsync(record, moved, overwrite: true));
        Assert.Equal("UnsafeExport", unsafeTarget.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(moved));
        string target = Path.Combine(temp, "report.txt");
        await File.WriteAllTextAsync(target, "existing");
        var conflict = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.ExportAsync(record, target, overwrite: false));
        Assert.Equal("CopyDestinationExists", conflict.Code);
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
        await store.ExportAsync(record, target, overwrite: true);
        Assert.Equal(await File.ReadAllTextAsync(store.FilePath(record)), await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task PollDetectsReplacementWithSameSizeAndRestoredTimestamp()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        var first = await store.InspectAsync(record);
        Assert.Same(first, await store.PollAsync(first));
        byte[] bytes = await File.ReadAllBytesAsync(first.FilePath);
        bytes[0] ^= 1;
        string replacement = first.FilePath + ".tmp";
        await File.WriteAllBytesAsync(replacement, bytes);
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(first.FilePath));
        File.Move(replacement, first.FilePath, overwrite: true);
        Assert.Equal(WorkCopyState.Modified, (await store.PollAsync(first)).State);
        using var held = File.Open(first.FilePath, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.Equal(WorkCopyState.Unavailable, (await store.PollAsync(first)).State);
    }

    [Fact]
    public async Task RetainedCopyKeepsAStricterDownloadZone()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        record = record with { SourceZone = "3" };
        string mark = store.FilePath(record) + ":Zone.Identifier";
        const string restricted = "[ZoneTransfer]\r\nZoneId=4\r\n";
        await File.WriteAllTextAsync(mark, restricted);
        await store.RestoreSourceMarkAsync(record);
        Assert.Equal(restricted, await File.ReadAllTextAsync(mark));
        string destination = Path.Combine(temp, "saved.txt");
        await store.ExportAsync(record, destination, overwrite: false);
        Assert.Contains("ZoneId=4", await File.ReadAllTextAsync(destination + ":Zone.Identifier"));
    }

    [Fact]
    public async Task MissingCopyFolderStaysWithinItsRecordAndMismatchedPayloadCannotBeLaunched()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string file = store.FilePath(record);
        Assert.Equal(Path.GetDirectoryName(file), store.FindCopyFolder(record));
        File.Delete(file);
        Directory.Delete(Path.GetDirectoryName(file)!);
        Assert.Equal(Path.Combine(store.Root, record.Id, "content"), store.FindCopyFolder(record));
        var forged = record with { RelativePath = "content/other.exe" };
        Assert.Equal("UnsafePath", Assert.Throws<ArchiveOperationException>(() => store.FilePath(forged)).Code);
        await File.WriteAllTextAsync(Path.Combine(store.Root, record.Id, "workcopy.json"), System.Text.Json.JsonSerializer.Serialize(forged));
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task RecoveryPagesCanContinuePastUnreadableRecordsAndReleaseOnCancel()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        var expected = new HashSet<string> { record.Id };
        for (int i = 0; i < 6; i++)
        {
            string id = Guid.NewGuid().ToString("N");
            string folder = Directory.CreateDirectory(Path.Combine(store.Root, id)).FullName;
            if (i % 2 == 0) continue;
            var copy = record with { Id = id };
            await File.WriteAllTextAsync(Path.Combine(folder, "workcopy.json"), System.Text.Json.JsonSerializer.Serialize(copy));
            expected.Add(id);
        }
        var pages = new List<WorkCopyListing>();
        await foreach (var page in store.ReadPagesAsync(pageSize: 2)) pages.Add(page);
        Assert.Equal(4, pages.Count);
        Assert.All(pages.Take(3), page => Assert.True(page.HasMore));
        Assert.False(pages[^1].HasMore);
        Assert.Equal(expected.Order(), pages.SelectMany(p => p.Records).Select(r => r.Id).Order());
        using var stop = new CancellationTokenSource();
        await using (var cursor = store.ReadPagesAsync(1, stop.Token).GetAsyncEnumerator())
        {
            Assert.True(await cursor.MoveNextAsync());
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cursor.MoveNextAsync().AsTask());
        }
        Directory.Move(store.Root, store.Root + "-released");
    }

    [Fact]
    public async Task HardLinkedReplacementCannotBeExportedOrGivenADownloadMark()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string file = store.FilePath(record);
        File.Delete(file);
        string other = Path.Combine(temp, "other.txt");
        await File.WriteAllTextAsync(other, "external");
        Assert.True(CreateCopyTestLink(file, other, IntPtr.Zero));
        Assert.Equal(WorkCopyState.Unavailable, (await store.InspectAsync(record)).State);
        var denied = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.ExportAsync(record, Path.Combine(temp, "saved.txt")));
        Assert.Equal("UnsafePath", denied.Code);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => store.RestoreSourceMarkAsync(record with { SourceZone = "3" }));
        Assert.False(File.Exists(other + ":Zone.Identifier"));
        Assert.Equal("external", await File.ReadAllTextAsync(other));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CreateCopyTestLink(string link, string existing, IntPtr reserved);

    [Fact]
    public async Task ForgedRelativePathCannotEscapeWorkingCopyFolder()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        var error = Assert.Throws<ArchiveOperationException>(() => store.FilePath(record with { RelativePath = "../outside.txt" }));
        Assert.Equal("UnsafePath", error.Code);
    }

    [Fact]
    public async Task ReplacedArchiveWithSameTimestampDoesNotReuseEntryIds()
    {
        string archive = MakeZip("a.txt");
        var snapshot = await client.ScanAsync(archive);
        var timestamp = File.GetLastWriteTimeUtc(archive);
        string replacement = MakeZip("b.txt");
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Move(replacement, archive, overwrite: true);
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ExtractAsync(archive,
            new(Path.Combine(temp, "output"), SelectedIds: [0], ExpectedIdentity: snapshot.Identity)));
        Assert.Equal("ArchiveChanged", error.Code);
    }
}
