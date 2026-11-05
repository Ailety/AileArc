using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AileArc.Core;
using AileArc.Core.Extraction;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckExtractionResultsAsync(MainWindow window, ClientApp app, string root)
    {
        var worker = new ArchiveWorkerClient(Path.Combine(AppContext.BaseDirectory, "Worker", "AileArc.Worker.exe"));
        string source = Path.Combine(root, "密码样本.txt");
        const string secret = "public-test-password";
        await File.WriteAllTextAsync(source, "public fixture");
        string encrypted = Path.Combine(root, "加密数据.zip");
        await worker.CreateAsync(new([source], encrypted, Password: secret));
        await window.TestOpenAsync(encrypted);
        var opened = new List<string>();
        window.OpenFolder = opened.Add;
        string target = Path.Combine(root, "密码重试");

        async Task<int> ExtractWithPassword(string value, string output)
        {
            int prompts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                if (app.Windows.OfType<PasswordDialog>().FirstOrDefault() is not { } prompt) return;
                var input = (PasswordBox)LogicalTreeHelper.FindLogicalNode(prompt, "ArchivePassword");
                input.Password = value;
                prompt.DialogResult = ++prompts == 1;
            };
            timer.Start();
            try { await window.ExtractToAsync(output, false, false, openFolder: true); }
            finally { timer.Stop(); }
            return prompts;
        }

        Require(await ExtractWithPassword("wrong", target) == 1 && window.LastExtraction is { Completed: 0 } failed &&
            failed.Failures.Any(f => f.Code == "WrongPasswordOrDamaged") && opened.Count == 0 &&
            !File.Exists(Path.Combine(target, Path.GetFileName(source))), "wrong ZIP password does not publish or launch a file");
        await ObserveDialogAsync(app, ClientApp.Text["ClientResult"], () =>
        {
            window.ResultButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
        }, dialog => Require(dialog.Body.Children.OfType<TextBox>().Single().Text.Contains(Path.GetFileName(source)) &&
            dialog.Body.Children.OfType<TextBlock>().Any(t => t.Text == ClientApp.Text["ClientPasswordRetryHint"]), "failure report identifies the entry and retry action"));
        Require(await ExtractWithPassword(secret, target) == 1 && File.ReadAllText(Path.Combine(target, Path.GetFileName(source))) == "public fixture" &&
            opened.SequenceEqual([target]), "retry in the same session accepts a new password");
        Require(await ExtractWithPassword("unused", target + "-cached") == 0, "verified password reused in this session");

        async Task Dialog(bool expected, bool requested, bool remember, bool accept, string folder)
        {
            await ObserveDialogAsync(app, ClientApp.Text["Extract"], window.ExtractCurrentSelectionAsync, dialog =>
            {
                var boxes = dialog.Body.Children.OfType<CheckBox>().ToArray();
                Require(boxes[0].IsChecked == expected && boxes[1].IsChecked != true, "dialog reads the stored choice");
                boxes[0].IsChecked = requested; boxes[1].IsChecked = remember;
                dialog.Body.Children.OfType<TextBox>().Single().Text = folder;
            }, accept);
        }
        opened.Clear();
        await Dialog(false, true, true, false, target);
        Require(!app.OpenAfterExtraction && opened.Count == 0, "cancelled dialog discards preference changes");
        await Dialog(false, true, true, true, target + "-remember");
        Require(app.OpenAfterExtraction && opened.SequenceEqual([target + "-remember"]), "remembered choice opens the successful destination");
        await Dialog(true, false, false, true, target + "-once");
        Require(app.OpenAfterExtraction && opened.Count == 1, "one-time override leaves the stored choice intact");
        window.ConflictOverride = (_, _) => Task.FromResult(new ConflictDecision(ConflictChoice.Skip));
        await Dialog(true, true, false, true, target);
        Require(window.LastExtraction is { Completed: 0, Skipped: 1 } && opened.Count == 1, "all-skipped extraction does not open Explorer");
        window.ConflictOverride = null;
        await Dialog(true, false, true, true, target + "-off");
        Require(!app.OpenAfterExtraction, "remembered open-after-extract can be disabled");

        string damaged = Path.Combine(root, "部分损坏.zip");
        byte[] marker = "intentionally damaged test data"u8.ToArray();
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("bad.bin", CompressionLevel.NoCompression).Open()) stream.Write(marker);
            using (var stream = zip.CreateEntry("bad.bin", CompressionLevel.NoCompression).Open()) stream.WriteByte(17);
            using (var stream = zip.CreateEntry("good.bin", CompressionLevel.NoCompression).Open()) stream.WriteByte(42);
        }
        byte[] bytes = await File.ReadAllBytesAsync(damaged);
        int at = bytes.AsSpan().IndexOf(marker);
        Require(at >= 0, "stored ZIP contains test payload");
        bytes[at] ^= 1;
        await File.WriteAllBytesAsync(damaged, bytes);
        await window.TestOpenAsync(damaged);
        string partial = target + "-partial";
        await window.ExtractToAsync(partial, false, false, openFolder: true);
        Require(window.LastExtraction is { Completed: 2 } mixed && mixed.Failures.Single().Code == "IntegrityFailed" &&
            File.ReadAllBytes(Path.Combine(partial, "bad.bin")).SequenceEqual(new byte[] { 17 }) && File.ReadAllBytes(Path.Combine(partial, "good.bin")).SequenceEqual(new byte[] { 42 }) &&
            opened.Count == 1 && window.ResultButton.Visibility == Visibility.Visible, "partial failure keeps verified output and report, without opening Explorer");
        string retry = partial + "-retry";
        await ObserveDialogAsync(app, ClientApp.Text["Extract"], () =>
            ObserveDialogAsync(app, ClientApp.Text["ClientResult"], window.ShowExtractionResultAsync, details =>
                details.Body.Children.OfType<Button>().Single(b => Equals(b.Content, ClientApp.Text.Format("ClientRetryFailures", 1)))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent))), confirm =>
        {
            var output = confirm.Body.Children.OfType<TextBox>().Single();
            Require(output.Text == partial, "ordinary retry starts at the previous destination");
            output.Text = retry;
        }, accept: true);
        Require(window.LastExtraction is { Completed: 0 } retried && retried.Failures.Single().EntryId == 0 &&
            Directory.GetFiles(retry).Length == 0 && File.ReadAllBytes(Path.Combine(partial, "bad.bin")).Single() == 17,
            "retry sends only the failed ID even when a successful entry has the same path");

        string empty = Path.Combine(root, "空.zip");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        await window.TestOpenAsync(empty);
        string unused = Path.Combine(root, "空包目标");
        await window.ExtractToAsync(unused, true, false, openFolder: true);
        Require(window.OpenedPath == empty && window.LastExtraction is null && window.ResultButton.Visibility == Visibility.Collapsed &&
            window.StatusText.Text == ClientApp.Text["ClientArchiveEmpty"] && opened.Count == 1 && !Directory.Exists(unused),
            "empty archive clears old results and neither creates nor opens a destination");
    }
}
