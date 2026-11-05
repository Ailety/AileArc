using System.IO.Compression;
using AileArc.Core;
using AileArc.Core.Extraction;
using AileArc.Core.WorkCopies;
using AileArc.Shared;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Fact]
    public async Task DamagedStoredZipIsOnlyRetainedThroughPrivateOpenPreparation()
    {
        string path = Path.Combine(temp, "damaged.zip");
        byte[] content = "decoded fixture data"u8.ToArray();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("notes.txt", CompressionLevel.NoCompression).Open();
            stream.Write(content);
        }
        byte[] original = await File.ReadAllBytesAsync(path);
        int at = original.AsSpan().IndexOf(content);
        Assert.True(at >= 0); original[at] ^= 1; content[0] ^= 1;
        await File.WriteAllBytesAsync(path, original);
        await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var scan = await client.ScanAsync(path);
        var entry = Assert.Single(scan.Entries);
        string target = Directory.CreateDirectory(Path.Combine(temp, "extract")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "notes.txt"), "keep");
        var strict = await client.ExtractAsync(path, new(target, false, ConflictChoice.Replace));
        Assert.Equal(0, strict.Completed); Assert.Equal("IntegrityFailed", Assert.Single(strict.Failures).Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "notes.txt")));
        var store = new WorkCopyStore(Path.Combine(temp, "copies"));
        var record = await store.PrepareForOpenAsync(client, path, scan.Identity!, entry, 0, null);
        Assert.Equal(2, record.Version);
        Assert.Equal(new UnverifiedContent("IntegrityFailed", (ulong)content.Length, (ulong)content.Length), record.Damage);
        Assert.Equal(content, await File.ReadAllBytesAsync(store.FilePath(record)));
        Assert.Equal("3", record.SourceZone);
        var restarted = new WorkCopyStore(store.Root);
        var loaded = Assert.Single(await restarted.LoadAsync());
        Assert.Equal(record, loaded);
        // The strict preparation API must not inherit opt-in behavior from the cache.
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.PrepareAsync(path, scan.Identity!, entry,
            (_, _) => throw new InvalidOperationException("Should use the cached state")));
        Assert.Equal("IntegrityFailed", failure.Code);
        string saved = Path.Combine(temp, "saved.txt");
        await restarted.ExportAsync(loaded, saved, overwrite: false);
        Assert.Equal(content, await File.ReadAllBytesAsync(saved));
        Assert.Contains("ZoneId=3", await File.ReadAllTextAsync(saved + ":Zone.Identifier"));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Throws<ArchiveOperationException>(() => store.FilePath(record with { Version = 1 }));
        Assert.Throws<ArchiveOperationException>(() => store.FilePath(record with { Damage = null }));
        await restarted.RemoveUnchangedAsync(loaded);
        Assert.Empty(await restarted.LoadAsync());
        Assert.Equal(content, await File.ReadAllBytesAsync(saved));
    }

    [Theory]
    [InlineData("empty", "IntegrityFailed")]
    [InlineData("encrypted", "WrongPasswordOrDamaged")]
    [InlineData("exit", "WorkerFailed")]
    [InlineData("extra", "ProtocolMismatch")]
    [InlineData("method", "EntryMethodUnsupported")]
    [InlineData("overrun", "IntegrityFailed")]
    [InlineData("false-success", "IntegrityFailed")]
    public async Task IneligibleOrUnfinishedDecodeNeverYieldsAnOpenableFile(string fault, string code)
    {
        var store = new WorkCopyStore(Path.Combine(temp, "copies"));
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => store.PrepareForOpenAsync(SlowWorker(),
            Path.Combine(temp, "damaged-copy-" + fault), new("fixture", 0, 0), new(0, "partial.txt", false, 5, fault == "encrypted"), 0, "fixture-pass"));
        Assert.Equal(code, error.Code);
        var record = Assert.Single(await store.LoadAsync());
        Assert.Null(record.BaselineHash); Assert.Null(record.Damage);
        Assert.False(File.Exists(store.FilePath(record)));
        Assert.Empty(Directory.GetFiles(store.Root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TruncatedContentReportsActualLengthAndCancellationDiscardsTheCandidate()
    {
        var store = new WorkCopyStore(Path.Combine(temp, "copies"));
        var worker = SlowWorker();
        var entry = new ArchiveEntry(0, "partial.txt", false, 5, false);
        var identity = new ArchiveIdentity("fixture", 0, 0);
        var kept = await store.PrepareForOpenAsync(worker, Path.Combine(temp, "damaged-copy-short"), identity, entry, 0, null);
        Assert.Equal(new UnverifiedContent("EntryUnexpectedEnd", 2, 5), kept.Damage);
        Assert.Equal("AB", await File.ReadAllTextAsync(store.FilePath(kept)));
        string path = Path.Combine(temp, "damaged-copy-cancel");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var preparing = store.PrepareForOpenAsync(worker, path, identity, entry, 0, null, stop.Token);
        while (!File.Exists(path + ".ready") && !preparing.IsCompleted) await Task.Delay(10, stop.Token);
        Assert.True(File.Exists(path + ".ready")); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        var aborted = Assert.Single(await store.LoadAsync(), r => r.Id != kept.Id);
        Assert.False(File.Exists(store.FilePath(aborted))); Assert.Null(aborted.BaselineHash);
    }
}
