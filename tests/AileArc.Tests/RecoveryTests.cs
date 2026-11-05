using System.Diagnostics;
using AileArc.Core;
using AileArc.Core.Recovery;
using AileArc.Core.WorkCopies;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Fact]
    public async Task ExplicitCleanupRemovesOnlyUnchangedCopies()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        await store.RemoveUnchangedAsync(record);
        Assert.False(File.Exists(store.FilePath(record)));
        Assert.Empty(await store.LoadAsync());
        Assert.True(File.Exists(record.ArchivePath));
    }
    [Fact]
    public async Task CleanupRechecksChangesInsteadOfTrustingAnEarlierStatus()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        Assert.Equal(WorkCopyState.Unchanged, (await store.InspectAsync(record)).State);
        await File.WriteAllTextAsync(store.FilePath(record), "new edit after inspection");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.RemoveUnchangedAsync(record));
        Assert.Equal("CopyCleanupRefused", error.Code);
        Assert.Equal("new edit after inspection", await File.ReadAllTextAsync(store.FilePath(record)));
        Assert.Single(await store.LoadAsync());
    }
    [Fact]
    public async Task CleanupPreservesEditorBackups()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        string backup = store.FilePath(record) + ".bak";
        await File.WriteAllTextAsync(backup, "backup edits");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.RemoveUnchangedAsync(record));
        Assert.Equal("CopyCleanupExtraFiles", error.Code);
        Assert.Equal("backup edits", await File.ReadAllTextAsync(backup));
        Assert.True(File.Exists(store.FilePath(record)));
    }
    [Fact]
    public void NormalDisposalRemovesTemporaryDataAndJournal()
    {
        var store = new RecoveryStore(Path.Combine(temp, "journal"));
        string path;
        using (var file = TrackedTemporaryFile.Create(temp, "Test", store))
        {
            path = file.Path;
            File.WriteAllText(path, "in progress");
            Assert.Equal(RecoveryState.Busy, Assert.Single(store.List()).State);
            Assert.Equal(RecoveryState.Busy, store.Cleanup(file.Id));
        }
        Assert.False(File.Exists(path));
        Assert.Empty(store.List());
    }

    private async Task<(RecoveryStore Store, RecoveryRecord Record)> CrashProbeAsync(string? destination = null)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var store = new RecoveryStore(Path.Combine(temp, "journal"));
        var start = new ProcessStartInfo(Path.Combine(root, "tests", "AileArc.RecoveryProbe", "bin", configuration,
            "net10.0-windows10.0.22621.0", "AileArc.RecoveryProbe.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(destination ?? temp); start.ArgumentList.Add(store.Root);
        using var process = Process.Start(start)!;
        try
        {
            string? id = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrEmpty(id));
            Assert.Equal(RecoveryState.Busy, Assert.Single(store.List()).State);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        var item = Assert.Single(store.List());
        // Windows may briefly retain the terminated process's file handles after its exit notification.
        // Poll the real lease state with a bound; do not weaken the expected recovery result.
        var releaseDeadline = Stopwatch.StartNew();
        while (item.State == RecoveryState.Busy && releaseDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(20);
            item = Assert.Single(store.List());
        }
        Assert.Equal(RecoveryState.Recoverable, item.State);
        return (store, item.Record);
    }
    [Fact]
    public async Task ForcedExitRecoveryDoesNotTouchUnregisteredPartials()
    {
        string unrelated = Path.Combine(temp, ".ailearc-" + Guid.NewGuid().ToString("N") + ".partial");
        await File.WriteAllTextAsync(unrelated, "not registered");
        var (store, record) = await CrashProbeAsync();
        Assert.Equal(RecoveryState.Recoverable, store.Cleanup(record.Id));
        Assert.False(File.Exists(record.Path));
        Assert.Equal("not registered", await File.ReadAllTextAsync(unrelated));
        Assert.Empty(store.List());
    }
    [Fact]
    public async Task RecoveryRefusesAReplacedFileAtTheSamePath()
    {
        var (store, record) = await CrashProbeAsync();
        string replacement = Path.Combine(temp, "replacement.txt");
        await File.WriteAllTextAsync(replacement, "different file");
        File.Move(replacement, record.Path, overwrite: true);
        Assert.Equal(RecoveryState.Changed, store.Cleanup(record.Id));
        Assert.Equal("different file", await File.ReadAllTextAsync(record.Path));
        Assert.Single(store.List());
    }
    [Fact]
    public async Task RecoveryOfAlreadyCommittedFileOnlyRemovesTheJournal()
    {
        var (store, record) = await CrashProbeAsync();
        string committed = Path.Combine(temp, "finished.zip");
        File.Move(record.Path, committed);
        Assert.Equal(RecoveryState.Missing, store.Cleanup(record.Id));
        Assert.True(File.Exists(committed));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task CleanupRefusesAFileHeldByAnExternalApplication()
    {
        var (store, record) = await PrepareWorkCopyAsync();
        using (var read = new FileStream(store.FilePath(record), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => store.RemoveUnchangedAsync(record));
        Assert.True(File.Exists(store.FilePath(record)));
        Assert.Single(await store.LoadAsync());
    }
    [Fact]
    public async Task RecreatedParentCannotAuthorizeRemovingTheOldRecoveryRecord()
    {
        string folder = Directory.CreateDirectory(Path.Combine(temp, "target")).FullName;
        var (store, record) = await CrashProbeAsync(folder);
        string moved = Path.Combine(temp, "moved");
        Directory.Move(folder, moved); Directory.CreateDirectory(folder);
        Assert.Equal(RecoveryState.Changed, Assert.Single(store.List()).State);
        Assert.Equal(RecoveryState.Changed, store.Cleanup(record.Id));
        Assert.True(File.Exists(Path.Combine(moved, Path.GetFileName(record.Path))));
        Assert.Single(store.List());
    }

    [Fact]
    public async Task MissingTargetDirectoryDoesNotDiscardRecoveryInformation()
    {
        string target = Path.Combine(temp, "target");
        string moved = Path.Combine(temp, "moved");
        Directory.CreateDirectory(target);
        var (store, record) = await CrashProbeAsync(target);
        Directory.Move(target, moved);
        Assert.Equal(RecoveryState.Changed, Assert.Single(store.List()).State);
        Assert.Equal(RecoveryState.Changed, store.Cleanup(record.Id));
        Assert.Single(store.List());
        Assert.True(File.Exists(Path.Combine(moved, Path.GetFileName(record.Path))));
    }
    [Fact]
    public async Task ReplacedTargetDirectoryJunctionCannotRedirectRecoveryDeletion()
    {
        string target = Path.Combine(temp, "target");
        string moved = Path.Combine(temp, "moved");
        Directory.CreateDirectory(target);
        var (store, record) = await CrashProbeAsync(target);
        Directory.Move(target, moved);
        JunctionFixture.Create(target, moved);
        try
        {
            Assert.Equal(RecoveryState.Changed, store.Cleanup(record.Id));
            Assert.True(File.Exists(Path.Combine(moved, Path.GetFileName(record.Path))));
        }
        finally { Directory.Delete(target); }
    }

    [Fact]
    public async Task ConfirmationCannotFollowARewrittenJournalToAnotherFile()
    {
        var (store, record) = await CrashProbeAsync();
        var shown = Assert.Single(store.List());
        string other = Path.Combine(Directory.CreateDirectory(Path.Combine(temp, "elsewhere")).FullName, Path.GetFileName(record.Path));
        await File.WriteAllTextAsync(other, "keep");
        string otherId;
        using (var file = File.OpenRead(other)) otherId = AileArc.Shared.ArchiveFileIdentity.Read(file.SafeFileHandle).FileId;
        var redirected = new RecoveryRecord(1, record.Id, other, otherId, record.Kind, record.CreatedUtc);
        await File.WriteAllTextAsync(Path.Combine(store.Root, record.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(redirected));
        Assert.Equal(RecoveryState.Changed, store.Cleanup(shown));
        Assert.True(File.Exists(record.Path)); Assert.Equal("keep", await File.ReadAllTextAsync(other));
        Assert.Equal(other, Assert.Single(store.List()).Record.Path);
    }

    [Fact]
    public async Task ReplacingJournalWithIdenticalJsonStillRequiresANewReview()
    {
        var (store, record) = await CrashProbeAsync();
        var shown = Assert.Single(store.List());
        string journal = Path.Combine(store.Root, record.Id + ".json");
        File.Copy(journal, journal + ".replacement");
        File.Move(journal + ".replacement", journal, true);
        Assert.Equal(RecoveryState.Changed, store.Cleanup(shown));
        Assert.True(File.Exists(record.Path));
        Assert.Equal(RecoveryState.Recoverable, store.Cleanup(Assert.Single(store.List())));
    }

    [Fact]
    public async Task CleanupRechecksTheDisplayedVersionAndMissingOnlyScope()
    {
        var (store, record) = await CrashProbeAsync();
        var first = Assert.Single(store.List());
        await File.AppendAllTextAsync(record.Path, "modified since inspection");
        Assert.Equal(RecoveryState.Changed, store.Cleanup(first));
        File.Move(record.Path, record.Path + ".moved");
        var missing = Assert.Single(store.List());
        Assert.Equal(RecoveryState.Missing, missing.State);
        File.Move(record.Path + ".moved", record.Path);
        Assert.Equal(RecoveryState.Changed, store.Cleanup(missing));
        Assert.True(File.Exists(record.Path));
        var current = Assert.Single(store.List());
        Assert.Equal(RecoveryState.Recoverable, store.Cleanup(current));
        Assert.Equal(RecoveryState.NoRecord, store.Cleanup(current));
    }

    [Fact]
    public async Task StaleLeaseCanBeInspectedWithoutChangingAnUnownedLock()
    {
        var (store, record) = await CrashProbeAsync();
        string lease = Path.Combine(store.Root, record.Id + ".lock");
        await File.WriteAllTextAsync(lease, "external marker");
        Assert.Equal(RecoveryState.Unverified, Assert.Single(store.List()).State);
        Assert.Equal(RecoveryState.Unverified, store.Cleanup(record.Id));
        Assert.Equal("external marker", await File.ReadAllTextAsync(lease));
        await File.WriteAllBytesAsync(lease, []);
        var item = Assert.Single(store.List());
        Assert.Equal(RecoveryState.Recoverable, item.State);
        Assert.Equal(RecoveryState.Recoverable, store.Cleanup(item));
        Assert.True(File.Exists(lease)); Assert.False(File.Exists(record.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRegistrationRequiresTheActualFileIdentity(bool removed)
    {
        var (store, record) = await CrashProbeAsync();
        var legacy = new { Version = 1, record.Id, record.Path, record.FileId, record.Kind, record.CreatedUtc };
        await File.WriteAllTextAsync(Path.Combine(store.Root, record.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(legacy));
        if (removed) File.Delete(record.Path);
        var item = Assert.Single(store.List());
        Assert.Equal(removed ? RecoveryState.Unverified : RecoveryState.Recoverable, item.State);
        Assert.Equal(item.State, store.Cleanup(item));
        Assert.Equal(removed, File.Exists(Path.Combine(store.Root, record.Id + ".json")));
    }

    [Fact]
    public void OversizedRegistrationCannotCreateAnUninspectableStagingFile()
    {
        var store = new RecoveryStore(Path.Combine(temp, "journal"));
        var error = Assert.Throws<ArchiveOperationException>(() => TrackedTemporaryFile.Create(temp, new string('测', 30000), store));
        Assert.Equal("ResourceLimit", error.Code);
        Assert.Empty(Directory.GetFiles(temp, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void MalformedRecordsAreVisibleButCannotAuthorizeCleanupAndPagingCancels()
    {
        var store = new RecoveryStore(Directory.CreateDirectory(Path.Combine(temp, "broken")).FullName);
        for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(store.Root, Guid.NewGuid().ToString("N") + ".json"), "{");
        var pages = store.ReadPages(2).ToArray();
        Assert.Equal(new[] { true, true, false }, pages.Select(p => p.HasMore));
        var items = pages.SelectMany(p => p.Items).ToArray();
        Assert.Equal(5, items.Select(i => i.Record.Id).Distinct().Count());
        foreach (var item in items)
        {
            Assert.Equal("", item.Record.Path); Assert.Equal(RecoveryState.Unverified, store.Cleanup(item));
        }
        using var cancel = new CancellationTokenSource();
        using (var cursor = store.ReadPages(1, cancel.Token).GetEnumerator())
        {
            Assert.True(cursor.MoveNext()); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => cursor.MoveNext());
        }
        Directory.Move(store.Root, store.Root + "-released");
    }

    [Fact]
    public async Task CancellingCleanupKeepsTheFileAndRecoveryRecord()
    {
        var (store, record) = await CrashProbeAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Cleanup(record.Id, cancelled.Token));
        Assert.True(File.Exists(record.Path));
        Assert.Single(store.List());
    }
}
