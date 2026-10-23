using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AileArc.Core;
using AileArc.Core.Compression;
using AileArc.Core.Lifecycle;
using AileArc.Core.Recovery;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    internal static int RecoveryProbe(string target, string journal)
    {
        using var file = TrackedTemporaryFile.Create(target, "Test", new RecoveryStore(journal));
        File.WriteAllText(file.Path, "generated crash fixture");
        Console.WriteLine(file.Id); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return 0;
    }
    private static async Task CheckP3Async(MainWindow window, ClientApp app, string root)
    {
        string source = Path.Combine(root, "创建测试.txt"); await File.WriteAllTextAsync(source, "P3 public fixture");
        string zip = Path.Combine(root, "created.zip"), seven = Path.Combine(root, "created.7z");
        var worker = new ArchiveWorkerClient(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
        await window.CreateArchiveAsync(new([source], zip));
        Require(window.LastCreated?.Entries == 1 && (await worker.ScanAsync(zip)).Format == "ZIP", "WPF creates and verifies ZIP");
        byte[] original = await File.ReadAllBytesAsync(zip);
        await window.CreateArchiveAsync(new([source], zip));
        byte[] preserved = await File.ReadAllBytesAsync(zip);
        Require(window.LastCreated is null && original.SequenceEqual(preserved), "creation does not overwrite without consent");
        await window.CreateArchiveAsync(new([source], seven, "7Z", "Fast", "public-test-password", true));
        Require(window.LastCreated?.Entries == 1 && (await worker.ScanAsync(seven, password: "public-test-password")).Format == "7Z", "encrypted 7Z creation");
        Require(await File.ReadAllTextAsync(source) == "P3 public fixture", "creation preserves source");

        app.Recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        string unrelated = Path.Combine(root, "unregistered.partial"); await File.WriteAllTextAsync(unrelated, "keep");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "--self-test", "recovery-probe", root, app.Recovery.Root }) start.ArgumentList.Add(arg);
        string id;
        using (var child = Process.Start(start)!)
        {
            try
            {
                id = (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
                Require(!string.IsNullOrEmpty(id), "crash probe registered");
                await window.CleanupRecoveryAsync(id);
                Require(window.LastCleanup == RecoveryState.Busy, "live recovery lease cannot be cleaned");
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        }
        var timeout = Stopwatch.StartNew();
        while (app.Recovery.List().Single().State == RecoveryState.Busy && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
        string staged = app.Recovery.List().Single().Record.Path;
        await window.CleanupRecoveryAsync(id);
        Require(window.LastCleanup == RecoveryState.Recoverable && !File.Exists(staged) && File.Exists(unrelated), "only registered interrupted output cleaned");

        await window.TestOpenAsync(zip);
        window.LaunchFile = (_, _) => { };
        await window.OpenExternalAsync(0);
        string copy = window.LastCopyPath!;
        var record = (await app.WorkCopies.LoadAsync()).Single(r => app.WorkCopies.FilePath(r) == copy);
        await window.CleanupCopyAsync(record);
        Require(!File.Exists(copy) && File.Exists(zip), "explicit unchanged-copy cleanup");
        await window.OpenExternalAsync(0);
        copy = window.LastCopyPath!; record = (await app.WorkCopies.LoadAsync()).Single(r => app.WorkCopies.FilePath(r) == copy);
        await File.WriteAllTextAsync(copy, "changed since inspection");
        await window.CleanupCopyAsync(record);
        Require(File.Exists(copy), "modified copy cleanup refused");

        int count = app.Windows.OfType<MainWindow>().Count();
        await app.OpenPathAsync(zip);
        Require(app.Windows.OfType<MainWindow>().Count() == count, "same-path activation reuses window");
        string alias = Path.Combine(root, "alias.zip");
        Require(CreateHardLink(alias, zip, IntPtr.Zero), "create local alias fixture");
        await Task.WhenAll(app.OpenPathAsync(alias), app.OpenPathAsync(zip));
        Require(app.Windows.OfType<MainWindow>().Count() == count, "serialized file-identity alias activation");
        var other = app.CreateWindow(); other.Show();
        Require(app.TryBeginCleanup(window), "acquire process cleanup gate");
        try
        {
            string forbidden = Path.Combine(root, "must-not-start.zip");
            await other.CreateArchiveAsync(new([source], forbidden));
            Require(!File.Exists(forbidden) && !other.IsBusy, "cleanup blocks another window's write task");
        }
        finally { app.EndCleanup(); }
        other.ApproveClose(); other.Close();

        string key = "AileArc-Wpf-Test-" + Guid.NewGuid().ToString("N");
        using (var broker = ActivationBroker.TryRegister(key)!)
        {
            broker.Listen(app.EnqueueActivation);
            await ActivationBroker.ForwardAsync(key, new([alias]));
            await ActivationBroker.ForwardAsync(key, new([], true));
            var deadline = Stopwatch.StartNew();
            while (app.Windows.OfType<MainWindow>().Count() != count + 1 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Require(app.Windows.OfType<MainWindow>().Count() == count + 1, "broker dispatches new window and deduplicates aliases");
        }
        foreach (var extra in app.Windows.OfType<MainWindow>().Where(w => w != window).ToArray()) { extra.ApproveClose(); extra.Close(); }
        var empty = ClientApp.ParseArguments(["--restore-session", "[{\"Path\":\"\",\"Empty\":true}]"]);
        Require(empty.Single().Empty, "restart preserves empty window");
        window.ConfirmOverride = (_, _) => false;
        await app.RestartAsync();
        Require(!app.Restarting && window.IsEnabled && window.IsVisible && File.Exists(copy), "declined restart restores all windows and keeps modified copy");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr reserved);
}
