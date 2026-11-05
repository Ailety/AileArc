using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AileArc.Core;
using AileArc.Core.Compression;
using AileArc.Core.Lifecycle;
using AileArc.Core.Recovery;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckEncryptedStartupAsync(ClientApp app, string path)
    {
        MainWindow? owner = null;
        bool ready = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (app.Windows.OfType<PasswordDialog>().FirstOrDefault() is not { IsVisible: true } prompt) return;
            owner = prompt.Owner as MainWindow;
            ready = owner is { IsVisible: true, IsBusy: true };
            ((PasswordBox)LogicalTreeHelper.FindLogicalNode(prompt, "ArchivePassword")).Password = "public-test-password";
            prompt.DialogResult = true; timer.Stop();
        };
        timer.Start();
        try
        {
            await app.OpenPathAsync(path);
            Require(ready && owner?.OpenedPath == path && owner.TestGrid.Items.Count == 1,
                "startup overlaps window creation without presenting an unowned password prompt");
        }
        finally { timer.Stop(); if (owner is not null) { owner.ApproveClose(); owner.Close(); } }
    }

    internal static int RecoveryProbe(string target, string journal)
    {
        using var file = TrackedTemporaryFile.Create(target, "Test", new RecoveryStore(journal), outputName: "恢复样本.zip");
        File.WriteAllText(file.Path, "generated crash fixture");
        Console.WriteLine(file.Id); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return 0;
    }
    private static async Task CheckP3Async(MainWindow window, ClientApp app, string root)
    {
        string source = Path.Combine(root, "创建测试.txt"); await File.WriteAllTextAsync(source, "P3 public fixture");
        string zip = Path.Combine(root, "created.zip"), seven = Path.Combine(root, "created.7z");
        var worker = new ArchiveWorkerClient(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
        string excluded = Path.Combine(root, "移除.txt");
        await File.WriteAllTextAsync(excluded, "excluded fixture");
        await ObserveDialogAsync(app, ClientApp.Text["CreateArchive"], () => window.ShowCreateDialogAsync([source, excluded]), dialog =>
        {
            var list = dialog.Body.Children.OfType<ListBox>().Single();
            list.SelectedItem = excluded;
            var output = dialog.Body.Children.OfType<TextBox>().Single();
            output.Text = "relative.zip";
            Require(!dialog.Accept.IsEnabled, "creation rejects a relative destination");
            output.Text = zip;
            Require(Equals(list.SelectedItem, excluded), "editing the destination preserves source selection");
            var remove = dialog.Body.Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
                .Single(b => Equals(b.Content, ClientApp.Text["ClientRemoveSources"]));
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(list.Items.Cast<string>().SequenceEqual([source]) && dialog.Accept.IsEnabled, "remove only selected input");
        }, accept: true);
        var created = await worker.ScanAsync(zip);
        Require(window.LastCreated?.Entries == 1 && created.Format == "ZIP" && created.Entries.Single().Path == Path.GetFileName(source),
            "created ZIP contains just the retained input");
        string? openedFolder = null; var previousOpenFolder = window.OpenFolder;
        window.OpenFolder = value => openedFolder = value;
        try
        {
            Require(window.CreatedFolderButton.IsVisible, "successful creation exposes the output folder");
            window.CreatedFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(openedFolder == root, "creation result opens its own destination directory");
        }
        finally { window.OpenFolder = previousOpenFolder; }
        string changing = Path.Combine(root, "正在替换.zip");
        await File.WriteAllTextAsync(changing, "first");
        var priorConfirm = window.ConfirmOverride;
        try
        {
            window.ConfirmOverride = (title, _) =>
            {
                Require(title == "ReplaceArchive", "overwrite requires explicit confirmation");
                string other = changing + ".new";
                File.WriteAllText(other, "created by another task"); File.Move(other, changing, true);
                return true;
            };
            await ObserveDialogAsync(app, ClientApp.Text["CreateArchive"], () => window.ShowCreateDialogAsync([source]), dialog =>
                dialog.Body.Children.OfType<TextBox>().Single().Text = changing, accept: true);
            Require(window.LastCreated is null && !window.CreatedFolderButton.IsVisible &&
                window.StatusText.Text == ClientApp.Text.Format("ClientIssueAtPath", ClientApp.Text["CompressionTargetChanged"], changing) && File.ReadAllText(changing) == "created by another task",
                "confirming a stale destination cannot replace the newer file or show a success action");
        }
        finally { window.ConfirmOverride = priorConfirm; }
        string alternate = Path.Combine(root, "保留输入另存.zip");
        bool retained = false;
        try
        {
            window.ConfirmOverride = (_, _) =>
            {
                var form = app.Windows.OfType<ActionDialog>().Single(d => d.Title == ClientApp.Text["CreateArchive"]);
                _ = form.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (form.IsVisible && !form.Body.IsEnabled && DateTime.UtcNow < until) await Task.Delay(10);
                    if (!form.IsVisible || !form.Body.IsEnabled) { if (form.IsVisible) form.Close(); return; }
                    retained = form.Body.Children.OfType<ListBox>().Single().Items.Cast<string>().SequenceEqual([source]);
                    form.Body.Children.OfType<TextBox>().Single().Text = alternate;
                    form.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                return false;
            };
            await ObserveDialogAsync(app, ClientApp.Text["CreateArchive"], () => window.ShowCreateDialogAsync([source]), dialog =>
                dialog.Body.Children.OfType<TextBox>().Single().Text = changing, accept: true);
            Require(retained && window.LastCreated?.Destination == alternate && File.ReadAllText(changing) == "created by another task",
                "declining overwrite keeps the same source list and lets the user choose another destination");
        }
        finally { window.ConfirmOverride = priorConfirm; }
        byte[] original = await File.ReadAllBytesAsync(zip);
        await window.CreateArchiveAsync(new([source], zip));
        byte[] preserved = await File.ReadAllBytesAsync(zip);
        Require(window.LastCreated is null && original.SequenceEqual(preserved), "creation does not overwrite without consent");
        await window.CreateArchiveAsync(new([source], seven, "7Z", "Fast", "public-test-password", true));
        Require(window.LastCreated?.Entries == 1 && (await worker.ScanAsync(seven, password: "public-test-password")).Format == "7Z", "encrypted 7Z creation");
        await CheckEncryptedStartupAsync(app, seven);
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
                await window.CleanupRecoveryAsync(app.Recovery.List().Single());
                Require(window.LastCleanup == RecoveryState.Busy, "live recovery lease cannot be cleaned");
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        }
        var timeout = Stopwatch.StartNew();
        while (app.Recovery.List().Single().State == RecoveryState.Busy && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
        string staged = app.Recovery.List().Single().Record.Path;
        await CheckRecoveryManagerAsync(window, app);
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
