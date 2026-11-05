using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AileArc.Core.Lifecycle;
using AileArc.Core.Extraction;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckShellAsync(MainWindow window, ClientApp app, string root)
    {
        Require(typeof(MainWindow).Assembly.GetName().Name == "AileArc", "stable installed executable identity");
        Require(!typeof(MainWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name?.StartsWith("Microsoft.UI") == true), "production UI has no WinUI reference");
        string source = Path.Combine(root, "创建测试.txt"), zip = Path.Combine(root, "created.zip");
        var request = ActivationRequest.Parse(["--create", "--", source], root);
        int before = app.Windows.OfType<MainWindow>().Count();
        await ObserveDialogAsync(app, ClientApp.Text["CreateArchive"], () => app.HandleActivationAsync(request), dialog =>
        {
            var selected = dialog.Body.Children.OfType<ListBox>().Single();
            Require(selected.Items.Cast<string>().SequenceEqual([source]), "Shell creation preselects source paths");
        });
        Require(app.Windows.OfType<MainWindow>().Count() == before + 1, "Shell create opens a dedicated window");
        foreach (var extra in app.Windows.OfType<MainWindow>().Where(w => w != window).ToArray()) { extra.ApproveClose(); extra.Close(); }
        await ObserveDialogAsync(app, ClientApp.Text["Extract"], () => app.HandleActivationAsync(new([zip], Action: ActivationAction.ExtractTo)), dialog =>
        {
            Require(dialog.Body.Children.OfType<TextBox>().Single().Text == root, "Shell extract-to uses archive directory initially");
        });
        Require(app.Windows.OfType<MainWindow>().Count() == before, "Shell extract-to reuses archive window");
        await app.HandleActivationAsync(new([zip], NewWindow: true));
        Require(app.Windows.OfType<MainWindow>().Count() == before + 1, "explicit new-window launch keeps its meaning");
        foreach (var extra in app.Windows.OfType<MainWindow>().Where(w => w != window).ToArray()) { extra.ApproveClose(); extra.Close(); }
        await app.HandleActivationAsync(new([zip], Action: ActivationAction.SmartExtract));
        await WaitForShellAsync(window);
        Require(window.LastExtraction is { Completed: 1, Cancelled: false }, "Shell smart extract reaches real Worker");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ConflictOverride = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(ConflictChoice.Cancel); };
        var active = window.ExtractToAsync(root, false, false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        window.QueueShellAction(ActivationAction.SmartExtract, [zip]);
        Require(window.IsBusy && !window.ShellActionPending, "Shell action does not replace an active task");
        window.ConfirmOverride = (_, _) => true; Require(await window.PrepareCloseAsync(), "active task safely cancels"); await active;
        window.ConflictOverride = null;

        string aliasFolder = Directory.CreateDirectory(Path.Combine(root, "右键别名")).FullName;
        string alias = Path.Combine(aliasFolder, "从这里解压.zip");
        Require(CreateHardLink(alias, zip, IntPtr.Zero), "create Shell source in a different directory");
        await ObserveDialogAsync(app, ClientApp.Text["Extract"], () => app.HandleActivationAsync(new([alias], Action: ActivationAction.ExtractTo)), dialog =>
            Require(dialog.Body.Children.OfType<TextBox>().Single().Text == aliasFolder, "Shell destination follows the invoked alias"));
        await app.HandleActivationAsync(new([alias], Action: ActivationAction.SmartExtract));
        await WaitForShellAsync(window);
        Require(window.OpenedPath == alias && File.ReadAllText(Path.Combine(aliasFolder, Path.GetFileName(source))) == "P3 public fixture",
            "smart extraction uses the clicked folder even when the original archive window is already open");
        string replacement = alias + ".tmp";
        using (var zipFile = System.IO.Compression.ZipFile.Open(replacement, System.IO.Compression.ZipArchiveMode.Create)) zipFile.CreateEntry("updated.txt");
        File.Move(replacement, alias, overwrite: true);
        await app.HandleActivationAsync(new([alias], Action: ActivationAction.SmartExtract));
        await WaitForShellAsync(window);
        Require(File.Exists(Path.Combine(aliasFolder, "updated.txt")) && window.TestGrid.Items.Cast<EntryView>().Single().Name == "updated.txt",
            "Shell re-reads a replacement before acting");
    }
    private static async Task WaitForShellAsync(MainWindow window)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (window.ShellActionPending && DateTime.UtcNow < limit) await Task.Delay(25);
        Require(!window.ShellActionPending, "Shell action finished within deadline");
    }
    private static async Task ObserveDialogAsync(ClientApp app, string title, Func<Task> action, Action<ActionDialog> inspect, bool accept = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            var dialog = app.Windows.OfType<ActionDialog>().FirstOrDefault(w => w.Title == title);
            if (dialog is null) return;
            timer.Stop();
            try
            {
                inspect(dialog);
                if (dialog.IsVisible && accept && dialog.BeforeAccept is not null) dialog.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (dialog.IsVisible) dialog.DialogResult = accept;
                done.TrySetResult();
            }
            catch (Exception error) { if (dialog.IsVisible) dialog.DialogResult = false; done.TrySetException(error); }
        };
        timer.Start();
        try { await action(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { timer.Stop(); }
        foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) await WaitForShellAsync(window);
    }
}
