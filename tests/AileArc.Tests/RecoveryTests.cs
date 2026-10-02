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
