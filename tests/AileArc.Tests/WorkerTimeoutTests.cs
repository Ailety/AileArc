using System.Diagnostics;
using AileArc.Core;
using AileArc.Core.Extraction;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    private ArchiveWorkerClient SlowWorker()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.Combine(root, "tests", "AileArc.RecoveryProbe", "bin", configuration,
            "net10.0-windows10.0.22621.0", "AileArc.RecoveryProbe.exe");
        return new(executable, TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData("slow-scan", 9)]
    [InlineData("slow-open", 1)]
    public async Task ActiveScanCanOutliveOneIdleInterval(string mode, int expected)
    {
        var scan = await SlowWorker().ScanAsync(Path.Combine(temp, mode)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(expected, scan.Entries.Count);
    }

    [Theory]
    [InlineData("partial-frame")]
    [InlineData("stuck-exit")]
    public async Task StalledScanEndsAndClientCanStartAnotherWorker(string mode)
    {
        var worker = SlowWorker();
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() =>
            worker.ScanAsync(Path.Combine(temp, mode)).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("TimedOut", error.Code);
        Assert.Single((await worker.ScanAsync(Path.Combine(temp, "healthy"))).Entries);
    }

    [Fact]
    public async Task BrokenProtocolDuringExtractionYieldsAPartialResult()
    {
        string output = Path.Combine(temp, "out");
        var result = await SlowWorker().ExtractAsync(Path.Combine(temp, "invalid-mid-extraction"), new(output, Smart: false));
        Assert.Equal("ProtocolMismatch", Assert.Single(result.Failures).Code);
        Assert.Equal(1, result.Completed);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Assert.Single(Directory.GetFiles(output))));
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("null-entry")]
    [InlineData("empty-batch")]
    [InlineData("negative-id")]
    [InlineData("invalid-file-time")]
    [InlineData("duplicate-prefix")]
    [InlineData("duplicate-sparse")]
    [InlineData("missing-id")]
    public async Task InvalidWorkerMetadataIsAnOperationFailure(string mode)
    {
        var worker = SlowWorker();
        string path = Path.Combine(temp, mode), output = path + "-out";
        var scan = await Assert.ThrowsAsync<ArchiveOperationException>(() => worker.ScanAsync(path));
        var extract = await Assert.ThrowsAsync<ArchiveOperationException>(() => worker.ExtractAsync(path, new(output)));
        Assert.Equal("ProtocolMismatch", scan.Code);
        Assert.Equal("ProtocolMismatch", extract.Code);
        Assert.False(Directory.Exists(output));
        Assert.Single((await worker.ScanAsync(Path.Combine(temp, "healthy"))).Entries);
    }

    [Fact]
    public async Task UnorderedUniqueIdsRemainValidAcrossWorkerFrames()
    {
        var scan = await SlowWorker().ScanAsync(Path.Combine(temp, "unordered-ids"));
        Assert.Equal(new long[] { 0, 20, 1 }, scan.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task CallerCancellationTerminatesReaderBlockedInsideAFrame()
    {
        string path = Path.Combine(temp, "partial-frame");
        using var cancel = new CancellationTokenSource();
        var scan = SlowWorker().ScanAsync(path, cancellationToken: cancel.Token);
        try
        {
            var limit = Stopwatch.StartNew();
            while (!File.Exists(path + ".pid") && !scan.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            int pid = int.Parse(await File.ReadAllTextAsync(path + ".pid"));
            using var child = Process.GetProcessById(pid);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(child.HasExited);
        }
        finally { cancel.Cancel(); }
    }

    [Fact]
    public async Task StalledExtractionPreservesCompletedOutputAndCleansPartialFile()
    {
        var output = Path.Combine(temp, "out");
        var result = await SlowWorker().ExtractAsync(MakeZip("unused"), new(output, Smart: false)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(result.Cancelled);
        Assert.Equal("TimedOut", Assert.Single(result.Failures).Code);
        Assert.Equal(1, result.Completed);
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(output))));
    }

    [Fact]
    public async Task TimeSpentChoosingAConflictActionDoesNotExpireTheWorker()
    {
        string output = Directory.CreateDirectory(Path.Combine(temp, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "ok.txt"), "existing");
        var result = await SlowWorker().ExtractAsync(Path.Combine(temp, "slow-decision"), new(output, Smart: false), async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new(ConflictChoice.Skip);
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Completed);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(output, "ok.txt")));
    }

    [Fact]
    public async Task WorkerIgnoringInputCannotBlockTheCompressionManifest()
    {
        string source = Directory.CreateDirectory(Path.Combine(temp, "sources")).FullName;
        for (int i = 0; i < 64; i++)
            File.WriteAllText(Path.Combine(source, new string('长', 80) + i + ".txt"), "data");
        string output = Path.Combine(temp, "new.zip");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() =>
            SlowWorker().CreateAsync(new([source], output)).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("TimedOut", error.Code);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }

    [Fact]
    public async Task StalledCreationDoesNotPublishTheUnconfirmedOutput()
    {
        string source = Path.Combine(temp, "source.txt"), output = Path.Combine(temp, "keep.zip");
        await File.WriteAllTextAsync(source, "sample");
        await File.WriteAllTextAsync(output, "existing");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() =>
            SlowWorker().CreateAsync(new([source], output, Overwrite: true)).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("TimedOut", error.Code);
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(temp, "*.partial"));
    }
}
