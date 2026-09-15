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
