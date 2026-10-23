using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AileArc.Core;
using AileArc.Core.Extraction;
using AileArc.Core.WorkCopies;

namespace AileArc.Client;

public partial class MainWindow
{
    private WorkCopyStore Copies => ((ClientApp)Application.Current).WorkCopies;
    private readonly Dictionary<string, WorkCopyRecord> openedCopies = [];
    private readonly DispatcherTimer copyTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool checkingCopies, closingCheck, allowClose;
    private TaskCompletionSource idleCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Action<string, IntPtr> LaunchFile { get; set; } = ExternalApplication.Open;
    internal Func<string, CancellationToken, Task<ConflictDecision>>? ConflictOverride { get; set; }
    internal ExtractionResult? LastExtraction { get; private set; }
    internal string? LastCopyPath { get; private set; }
    internal Func<string, string, bool>? ConfirmOverride { get; set; }

    private bool Confirm(string title, string detail, string accept = "Continue", string cancel = "Cancel")
    {
        if (ConfirmOverride is { } test) return test(title, detail);
        var dialog = new ActionDialog(this, title, accept, cancel); dialog.Explain(detail); return dialog.ShowDialog() == true;
    }
    private void InitializeOperations()
    {
        Closing += Window_Closing;
        Closed += (_, _) => copyTimer.Stop();
        copyTimer.Tick += async (_, _) =>
        {
            if (checkingCopies || IsBusy || closingCheck || closed || openedCopies.Count == 0) return;
            checkingCopies = true;
            try
            {
                var records = openedCopies.Values.ToArray();
                var states = await Task.Run(async () => { var list = new List<WorkCopyStatus>(); foreach (var record in records) list.Add(await Copies.InspectAsync(record)); return list; });
                if (!closed) CopyNotice.Text = states.Any(s => s.State != WorkCopyState.Unchanged) ? Text["ClientCopiesAttention"] : "";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArchiveOperationException) { }
            finally { checkingCopies = false; }
        };
        copyTimer.Start();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closingCheck || ((ClientApp)Application.Current).Restarting) return;
        if (await PrepareCloseAsync()) { allowClose = true; await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background); }
    }
    internal void ApproveClose() => allowClose = true;
    internal async Task<bool> PrepareCloseAsync()
    {
        if (closingCheck || OwnedWindows.Count > 0 || (ShellActionPending && operation is null)) return false;
        closingCheck = true;
        bool enabled = IsEnabled; IsEnabled = false;
        try
        {
            if (operation is not null)
            {
                if (!Confirm("TaskRunning", Text["CloseTaskHint"], "CancelAndExit", "KeepWaiting")) return false;
                var pending = idleCompletion.Task; operation.Cancel(); await pending;
            }
            var records = openedCopies.Values.ToArray();
            foreach (var record in records)
            {
                var state = await Task.Run(() => Copies.InspectAsync(record));
                if (state.State != WorkCopyState.Unchanged)
                    return Confirm("WorkCopies", Text["CloseCopiesHint"], "KeepCopiesAndExit", "Return");
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArchiveOperationException)
        { StatusText.Text = Text["CopyUnavailable"]; return false; }
        finally { closingCheck = false; IsEnabled = enabled; }
    }
    private async Task<T> WithPasswordAsync<T>(Func<string?, Task<T>> action, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await action(password); }
            catch (ArchiveOperationException error) when (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
            {
                token.ThrowIfCancellationRequested();
                var prompt = new PasswordDialog(this, error.Code);
                using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (prompt.IsVisible) prompt.Close(); })));
                bool accepted = prompt.ShowDialog() == true;
                var entered = accepted ? prompt.Value : null; prompt.Clear();
                if (!accepted) throw new OperationCanceledException(token);
                password = entered;
            }
        }
    }
    private Task<ConflictDecision> AskConflictAsync(string path, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        var dialog = new ActionDialog(this, "Conflict"); dialog.Explain(path);
        var choice = dialog.Choices(Text["Conflict"], [Text["RenameAutomatically"], Text["Skip"], Text["Replace"]]);
        var all = new CheckBox { Content = Text["ApplyToAll"] }; dialog.Body.Children.Add(all);
        using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
        bool accepted = dialog.ShowDialog() == true;
        return new ConflictDecision(accepted ? choice.SelectedIndex switch { 1 => ConflictChoice.Skip, 2 => ConflictChoice.Replace, _ => ConflictChoice.Rename } : ConflictChoice.Cancel, all.IsChecked == true);
    }).Task;

    private async void Extract_Click(object sender, RoutedEventArgs e) => await ShowExtractDialogAsync();
    internal async Task ShowExtractDialogAsync()
    {
        if (IsBusy || archive is null || OpenedPath is null) return;
        var dialog = new ActionDialog(this, "Extract", "Extract");
        var mode = dialog.Choices(Text["ClientExtractMode"], [Text["ExtractTo"], Text["ExtractSelected"], Text["SmartExtract"], Text["ClientExtractHere"]]);
        var destination = new TextBox { Text = Path.GetDirectoryName(OpenedPath), Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(destination, Text["Destination"]);
        dialog.Explain(Text["Destination"]); dialog.Body.Children.Add(destination);
        var browse = new Button { Content = Text["BrowseFolder"] };
        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(dialog) == true) destination.Text = picker.FolderName; };
        dialog.Body.Children.Add(browse);
        var hint = new TextBlock { Text = Text["ExtractHint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) }; dialog.Body.Children.Add(hint);
        mode.SelectionChanged += (_, _) => { bool here = mode.SelectedIndex == 3; destination.IsEnabled = browse.IsEnabled = !here; hint.Text = Text[mode.SelectedIndex == 2 ? "SmartExtractHint" : "ExtractHint"]; dialog.Accept.IsEnabled = mode.SelectedIndex != 1 || EntryGrid.SelectedItems.Count > 0; };
        var open = new CheckBox { Content = Text["ClientOpenDestination"] }; dialog.Body.Children.Add(open);
        if (dialog.ShowDialog() != true) return;
        await ExtractToAsync(mode.SelectedIndex == 3 ? Path.GetDirectoryName(OpenedPath)! : destination.Text ?? "", mode.SelectedIndex == 2, mode.SelectedIndex == 1, open.IsChecked == true);
        if (LastExtraction?.Failures.Count > 0)
        {
            var failures = new ActionDialog(this, "ClientResult", "Close", "Close");
            failures.Explain(StatusText.Text);
            failures.Body.Children.Add(new TextBox { Text = string.Join(Environment.NewLine, LastExtraction.Failures.Select(f => $"{f.Path}: {Text[f.Code]}")), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            failures.ShowDialog();
        }
    }
    internal Task ExtractToAsync(string target, bool smart, bool selectedOnly, bool openFolder = false)
    {
        if (archive is null || OpenedPath is null || IsBusy) return Task.CompletedTask;
        var snapshot = archive.Scan; string path = OpenedPath;
        var ids = EntryGrid.SelectedItems.Cast<EntryView>().Select(v => long.Parse(v.Row.Id, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var selection = archive.Index.Entries.Where(e => ids.Contains(e.Id)).ToArray();
        if (selectedOnly && selection.Length == 0) return Task.CompletedTask;
        LastExtraction = null;
        return RunAsync(Text["Extracting"], async token =>
        {
            int taskGeneration = generation;
            long[]? expanded = selectedOnly ? await Task.Run(() => ArchiveSelection.Expand(selection, snapshot.Entries), token) : null;
            long last = 0;
            var report = new Progress<ExtractionProgress>(p =>
            {
                if (closed || !IsBusy || generation != taskGeneration || token.IsCancellationRequested || Environment.TickCount64 - last < 100) return;
                last = Environment.TickCount64; StatusText.Text = Text.Format("ExtractionProgress", p.Completed, p.Skipped, p.Bytes / 1048576.0);
            });
            var result = await WithPasswordAsync(secret => Task.Run(() => worker.ExtractAsync(path,
                new ExtractionOptions(target, smart, SelectedIds: expanded, ExpectedIdentity: snapshot.Identity, NameCodePage: snapshot.NameCodePage),
                ConflictOverride ?? AskConflictAsync, report, token, secret), token), token);
            // A cancelled extraction still has a meaningful partial result and completed files.
            LastExtraction = result;
            StatusText.Text = Text.Format(result.Cancelled ? "ExtractionCancelled" : result.Failures.Count > 0 ? "ExtractionPartial" : "ExtractionDone", result.Completed, result.Skipped, result.Destination);
            return () => { if (openFolder && !result.Cancelled && result.Failures.Count == 0) LaunchFolder(result.Destination); };
        }, false);
    }
    private void LaunchFolder(string folder)
    {
        try { using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or IOException) { StatusText.Text = Text["ExternalOpenFailed"]; }
    }
    private async void OpenFile_Click(object sender, RoutedEventArgs e) => await ActivateAsync();
    internal Task OpenExternalAsync(long id)
    {
        if (archive?.Scan.Identity is null || OpenedPath is null || IsBusy) return Task.CompletedTask;
        var snapshot = archive.Scan; string path = OpenedPath;
        var entry = snapshot.Entries.SingleOrDefault(e => e.Id == id);
        if (entry is null || entry.IsDirectory) return Task.CompletedTask;
        if (ExternalOpenPolicy.RequiresConfirmation(entry.Path) && !Confirm("ConfirmExecutable", Text.Format("ExecutableHint", Path.GetFileName(entry.Path)), "OpenFile")) return Task.CompletedTask;
        return RunAsync(Text["PreparingExternal"], async token =>
        {
            var record = await WithPasswordAsync(secret => Task.Run(() => Copies.PrepareAsync(path, snapshot.Identity, entry,
                (destination, ct) => worker.ExtractAsync(path, new ExtractionOptions(destination, false, ByteLimit: WorkCopyStore.MaxFileBytes, SelectedIds: [id], ExpectedIdentity: snapshot.Identity, NameCodePage: snapshot.NameCodePage), cancellationToken: ct, password: secret), token), token), token);
            var state = await Task.Run(() => Copies.InspectAsync(record, token), token);
            if (state.State is WorkCopyState.Incomplete or WorkCopyState.Unavailable) throw new ArchiveOperationException("CopyUnavailable");
            await Task.Run(() => Copies.RestoreSourceMarkAsync(record, token), token);
            return () =>
            {
                openedCopies[record.Id] = record; LastCopyPath = state.FilePath;
                try { LaunchFile(state.FilePath, new WindowInteropHelper(this).Handle); StatusText.Text = Text["ExternalCopyNotice"]; }
                catch (Exception error) when (error is Win32Exception or IOException or System.Runtime.InteropServices.COMException) { StatusText.Text = Text["ExternalOpenFailed"]; }
            };
        }, false);
    }
    private async void Copies_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        IReadOnlyList<WorkCopyRecord> records = [];
        bool loaded = false;
        await RunAsync(Text["CheckingCopy"], async token => { records = await Task.Run(() => Copies.LoadAsync(token), token); return () => loaded = true; });
        if (!loaded) return;
        if (closed || records.Count == 0) { StatusText.Text = Text["NoWorkCopies"]; return; }
        var dialog = new ActionDialog(this, "WorkCopies", "SaveCopyAs", "Close");
        dialog.Explain(Text["CopiesRetainedHint"]);
        var selector = dialog.Choices(Text["WorkCopies"], records.Select(r => $"{r.CreatedUtc.ToLocalTime():g} · {r.EntryPath}").ToArray());
        var description = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 120 }; dialog.Body.Children.Add(description);
        var folder = new Button { Content = Text["OpenCopyFolder"] }; dialog.Body.Children.Add(folder);
        WorkCopyRecord? cleanup = null;
        var clean = new Button { Content = Text["CleanUnchangedCopy"], IsEnabled = false }; dialog.Body.Children.Add(clean);
        clean.Click += (_, _) => { cleanup = records[selector.SelectedIndex]; dialog.Close(); };
        folder.Click += (_, _) => LaunchFolder(Path.GetDirectoryName(Copies.FilePath(records[selector.SelectedIndex]))!);
        async Task Inspect()
        {
            int selected = selector.SelectedIndex; dialog.Accept.IsEnabled = clean.IsEnabled = false;
            try
            {
                var state = await Task.Run(() => Copies.InspectAsync(records[selected]));
                if (selector.SelectedIndex != selected) return;
                description.Text = Text["CopyState" + state.State] + Environment.NewLine + state.FilePath;
                dialog.Accept.IsEnabled = state.State is WorkCopyState.Unchanged or WorkCopyState.Modified;
                clean.IsEnabled = state.State == WorkCopyState.Unchanged && !((ClientApp)Application.Current).AnyTaskRunning;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArchiveOperationException) { description.Text = Text["CopyUnavailable"]; }
        }
        selector.SelectionChanged += async (_, _) => await Inspect();
        dialog.Loaded += async (_, _) => await Inspect();
        bool export = dialog.ShowDialog() == true;
        if (cleanup is not null)
        {
            var confirm = new ActionDialog(this, "CleanUnchangedCopy", "Clean"); confirm.Explain(Text["CleanupCopyHint"]);
            var editorsClosed = new CheckBox { Content = Text["EditorsClosed"] }; confirm.Body.Children.Add(editorsClosed);
            confirm.Accept.IsEnabled = false; editorsClosed.Click += (_, _) => confirm.Accept.IsEnabled = editorsClosed.IsChecked == true;
            if (confirm.ShowDialog() == true) await CleanupCopyAsync(cleanup);
            return;
        }
        if (!export) return;
        var record = records[selector.SelectedIndex];
        var picker = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(record.EntryPath), Filter = Text["ClientAllFilesFilter"], OverwritePrompt = true };
        if (picker.ShowDialog(this) == true) await ExportCopyAsync(record, picker.FileName);
    }
    internal Task ExportCopyAsync(WorkCopyRecord record, string destination) => RunAsync(Text["SaveCopyAs"], async token =>
    {
        await Task.Run(() => Copies.ExportAsync(record, destination, token), token);
        return () => StatusText.Text = Text.Format("CopyExported", destination);
    }, false);
}
