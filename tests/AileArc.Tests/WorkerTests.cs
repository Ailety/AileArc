using System.Diagnostics;
using System.IO.Compression;
using AileArc.Core;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "AileArc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly ArchiveWorkerClient client;
    public WorkerTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        client = new ArchiveWorkerClient(Path.Combine(root, "src", "AileArc.Worker", "bin", configuration,
            "net10.0-windows10.0.22621.0", "AileArc.Worker.exe"));
        Directory.CreateDirectory(temp);
    }

    private string MakeZip(params string[] paths)
    {
        var path = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string entryPath in paths)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
            writer.Write("AileArc 测试内容");
        }
        return path;
    }

    [Fact]
    public async Task NativeZipScanPreservesPathsAndDuplicateIds()
    {
        var result = await client.ScanAsync(MakeZip("中文/说明.txt", "a.txt", "a.txt", "line\nbreak.txt", "../unsafe.txt"));
        Assert.Equal("ZIP", result.Format);
        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(2, result.Entries.Count(e => e.Path == "a.txt"));
        Assert.Equal(5, result.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Contains(result.Entries, e => e.Path.Replace('\\', '/') == "中文/说明.txt");
        Assert.Contains(result.Entries, e => e.Path == "line\nbreak.txt");
        Assert.False(File.Exists(Path.Combine(temp, "unsafe.txt")));
    }

    [Fact]
    public async Task SignatureWinsOverWrongExtension()
    {
        string source = MakeZip("a.txt");
        string renamed = Path.ChangeExtension(source, ".rar");
        File.Move(source, renamed);
        Assert.Equal("ZIP", (await client.ScanAsync(renamed)).Format);
    }

    [Fact]
    public async Task EmptyZipIsValid()
    {
        Assert.Empty((await client.ScanAsync(MakeZip())).Entries);
    }

    [Fact]
    public async Task BadInputDoesNotPreventNextScan()
    {
        string bad = Path.Combine(temp, "bad.zip");
        await File.WriteAllTextAsync(bad, "not an archive");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(bad));
        Assert.Equal("UnsupportedOrDamaged", error.Code);
        Assert.Single((await client.ScanAsync(MakeZip("ok.txt"))).Entries);
    }

    [Fact]
    public async Task MissingFileHasSpecificError()
    {
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(Path.Combine(temp, "missing.zip")));
        Assert.Equal("FileNotFound", error.Code);
    }

    [Fact]
    public async Task CancelDuringScanThenOpenAgain()
    {
        var archive = MakeZip(Enumerable.Range(0, 5000).Select(i => $"folder/{i}.txt").ToArray());
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<int>(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ScanAsync(archive, progress, cancellation.Token));
        Assert.Single((await client.ScanAsync(MakeZip("after-cancel.txt"))).Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SolidSevenZipAndEncryptedHeaders(bool encrypted)
    {
        string source = Path.Combine(temp, "hello.txt");
        await File.WriteAllTextAsync(source, "你好 AileArc");
        string archive = Path.Combine(temp, "test.7z");
        var start = new ProcessStartInfo(Path.Combine(root, "third_party", "7zip", "bin", "7z.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "a", "-t7z", "-ms=on", archive, source }) start.ArgumentList.Add(argument);
        if (encrypted) { start.ArgumentList.Add("-pfixture-password"); start.ArgumentList.Add("-mhe=on"); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        Assert.Equal(0, process.ExitCode);
        if (encrypted)
        {
            var failure = await Assert.ThrowsAsync<ArchiveOperationException>(() => client.ScanAsync(archive));
            Assert.Equal("PasswordRequired", failure.Code);
            var unlocked = await client.ScanAsync(archive, password: "fixture-password");
            Assert.Single(unlocked.Entries);
        }
        else
        {
            var result = await client.ScanAsync(archive);
            Assert.Equal("7Z", result.Format);
            Assert.Equal("hello.txt", Assert.Single(result.Entries).Path);
        }
        var extracted = await client.ExtractAsync(archive, new(Path.Combine(temp, "sevenzip-output"), Smart: false),
            password: encrypted ? "fixture-password" : null);
        Assert.Empty(extracted.Failures);
        Assert.Equal("你好 AileArc", await File.ReadAllTextAsync(Path.Combine(extracted.Destination, "hello.txt")));
    }

    public void Dispose()
    {
        // This instance owns the randomly named test directory and no user files.
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
