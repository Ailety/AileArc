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
    private readonly Dictionary<string, WorkCopyStatus> openedCopies = [];
    private readonly DispatcherTimer copyTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool checkingCopies, closingCheck, allowClose;
    private TaskCompletionSource idleCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Action<string, IntPtr> LaunchFile { get; set; } = ExternalApplication.Open;
    internal Action<string> OpenFolder { get; set; } = folder =>
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        start.ArgumentList.Add(folder);
        using var process = Process.Start(start);
    };
    internal Func<string, CancellationToken, Task<ConflictDecision>>? ConflictOverride { get; set; }
    internal ExtractionResult? LastExtraction { get; private set; }
    private string? retryDestination;
    internal string? LastCopyPath { get; private set; }
    internal Func<string, string, bool>? ConfirmOverride { get; set; }

    private bool Confirm(string title, string detail, string accept = "Continue", string cancel = "Cancel", Window? owner = null)
    {
        if (ConfirmOverride is { } test) return test(title, detail);
        var dialog = new ActionDialog(owner ?? this, title, accept, cancel); dialog.Explain(detail); return dialog.ShowDialog() == true;
    }
    private void InitializeOperations()
    {
        Closing += Window_Closing;
        var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        Closed += (_, _) => { copyTimer.Stop(); lifetime.Cancel(); lifetime.Dispose(); };
        copyTimer.Tick += async (_, _) =>
        {
            if (checkingCopies || IsBusy || closingCheck || closed || openedCopies.Count == 0) return;
            checkingCopies = true;
            try
            {
                var previous = openedCopies.Values.ToArray();
                var states = await Task.Run(async () =>
                {
                    var list = new List<WorkCopyStatus>();
                    foreach (var status in previous) list.Add(await Copies.PollAsync(status, token));
                    return list;
                }, token);
                if (closed) return;
                for (int i = 0; i < states.Count; i++)
                {
                    string id = states[i].Record.Id;
                    if (ReferenceEquals(openedCopies.GetValueOrDefault(id), previous[i])) openedCopies[id] = states[i];
                }
                CopyNotice.Text = openedCopies.Values.Any(s => s.State != WorkCopyState.Unchanged) ? Text["ClientCopiesAttention"] : "";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
            var copies = openedCopies.Values.ToArray();
            foreach (var copy in copies)
            {
                var state = await Task.Run(() => Copies.InspectAsync(copy.Record));
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
                password = null;
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

    private enum ExtractionMode { All, Selected, Smart, Here, ParentFolder, Failed }
    private string ExtractionLabel(int count) => count == 0 ? Text["ClientExtractAll"] : Text.Format("ClientExtractSelection", count);
    private long[] SelectedIds() => EntryGrid.SelectedItems.Cast<EntryView>().Select(v => long.Parse(v.Row.Id, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    private async void Extract_Click(object sender, RoutedEventArgs e) => await ExtractCurrentSelectionAsync();
    internal Task ExtractCurrentSelectionAsync() => ShowExtractDialogAsync(EntryGrid.SelectedItems.Count == 0 ? ExtractionMode.All : ExtractionMode.Selected);
    private async void AllExtract_Click(object sender, RoutedEventArgs e) => await ShowExtractDialogAsync();
    private async void SmartExtract_Click(object sender, RoutedEventArgs e) => await ShowExtractDialogAsync(ExtractionMode.Smart);
    private async void HereExtract_Click(object sender, RoutedEventArgs e) => await ShowExtractDialogAsync(ExtractionMode.Here);
    private void ExtractMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = ExtractMore.ContextMenu; menu.PlacementTarget = ExtractMore;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }
    private async Task ShowExtractDialogAsync(ExtractionMode mode = ExtractionMode.All)
    {
        if (IsBusy || archive is null || OpenedPath is null) return;
        if (archive.Scan.Entries.Count == 0) { StatusText.Text = Text["ClientArchiveEmpty"]; return; }
        var dialog = new ActionDialog(this, "Extract", "Extract");
        var ids = mode == ExtractionMode.Selected ? SelectedIds() : null;
        if (mode == ExtractionMode.Failed)
        {
            if (LastExtraction is not { } result || !CanRetryFailures(result)) return;
            ids = result.Failures.Select(f => f.EntryId!.Value).ToArray();
        }
        if (mode == ExtractionMode.ParentFolder)
        {
            if (folderTree is null || EntryGrid.SelectedItems.Count != 1 || EntryGrid.SelectedItem is not EntryView item || item.Row.Type == "folder") return;
            string parent = ArchiveIndex.ParentOf(item.Path);
            ids = folderTree[parent].EntryId is long folderId ? [folderId] : null;
            dialog.Explain(parent.Length == 0 ? Text["ClientParentIsRoot"] : Text.Format("ClientParentScope", parent));
        }
        if (ids is { Length: 0 }) return;
        if (mode != ExtractionMode.ParentFolder) dialog.Explain(mode switch
        {
            ExtractionMode.Smart => Text["SmartExtract"],
            ExtractionMode.Here => Text["ClientExtractHere"],
            ExtractionMode.Failed => Text.Format("ClientRetryFailures", ids!.Length),
            _ => ExtractionLabel(ids?.Length ?? 0)
        });
        var destination = new TextBox { Text = (mode == ExtractionMode.Failed ? retryDestination : Path.GetDirectoryName(OpenedPath)) ?? "", Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(destination, Text["Destination"]);
        dialog.Explain(Text["Destination"]); dialog.Body.Children.Add(destination);
        var hint = new TextBlock { Text = Text["ClientLocalPathHint"], TextWrapping = TextWrapping.Wrap };
        dialog.Body.Children.Add(hint);
        void ValidateDestination()
        {
            dialog.Accept.IsEnabled = ActionDialog.IsLocalDestination(destination.Text);
            hint.Visibility = dialog.Accept.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        }
        destination.TextChanged += (_, _) => ValidateDestination();
        ValidateDestination();
        var browse = new Button { Content = Text["BrowseFolder"] };
        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(dialog) == true) destination.Text = picker.FolderName; };
        dialog.Body.Children.Add(browse);
        dialog.Explain(mode == ExtractionMode.Smart ? Text["SmartExtractHint"] : Text["ExtractHint"]);
        destination.IsEnabled = browse.IsEnabled = mode != ExtractionMode.Here;
        var app = (ClientApp)Application.Current;
        var open = new CheckBox { Content = Text["ClientOpenDestination"], IsChecked = app.OpenAfterExtraction };
        var remember = new CheckBox { Content = Text["ClientRememberOpenDestination"] };
        dialog.Body.Children.Add(open); dialog.Body.Children.Add(remember);
        if (dialog.ShowDialog() != true) return;
        bool openFolder = open.IsChecked == true;
        bool saveFailed = false;
        if (remember.IsChecked == true)
        {
            try { app.OpenAfterExtraction = openFolder; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { saveFailed = true; }
        }
        await ExtractCapturedAsync(destination.Text ?? "", mode == ExtractionMode.Smart, ids, openFolder, exactIds: mode == ExtractionMode.Failed);
        if (saveFailed) StatusText.Text += " · " + Text["SettingsFailed"];
        if (!closed && mode != ExtractionMode.Failed && LastExtraction?.Failures.Count > 0) await ShowExtractionResultAsync();
    }
    private string ExtractionSummary(ExtractionResult result)
    {
        if (!result.Cancelled && result.Failures.Count > 0 && result.Completed == 0)
            return Text.Format("ClientExtractionFailed", result.Failures.Count, result.Skipped);
        return Text.Format(result.Cancelled ? "ExtractionCancelled" : result.Failures.Count > 0 ? "ExtractionPartial" : "ExtractionDone",
            result.Completed, result.Skipped, result.Destination);
    }

    private static bool CanRetryFailures(ExtractionResult result) =>
        !result.Cancelled && result.Failures.Count > 0 && result.Failures.All(f => f.EntryId.HasValue);

    private async void Result_Click(object sender, RoutedEventArgs e) => await ShowExtractionResultAsync();
    internal async Task ShowExtractionResultAsync()
    {
        if (LastExtraction is not { } result || IsBusy) return;
        var dialog = new ActionDialog(this, "ClientResult", "Close", "Close");
        bool retry = false;
        dialog.Explain(ExtractionSummary(result));
        if (result.Failures.Count > 0)
        {
            if (result.Failures.Any(f => f.Code is "PasswordRequired" or "WrongPasswordOrDamaged"))
                dialog.Explain(Text["ClientPasswordRetryHint"]);
            AddFailureDetails(dialog, result.Failures);
            if (CanRetryFailures(result))
            {
                var button = new Button { Content = Text.Format("ClientRetryFailures", result.Failures.Count) };
                button.Click += (_, _) => { retry = true; dialog.DialogResult = true; };
                dialog.Body.Children.Add(button);
            }
        }
        if (result.Completed > 0)
        {
            var open = new Button { Content = Text["ClientShowOutputFolder"] };
            open.Click += (_, _) => LaunchFolder(result.Destination);
            dialog.Body.Children.Add(open);
        }
        dialog.ShowDialog();
        if (retry && ReferenceEquals(LastExtraction, result)) await ShowExtractDialogAsync(ExtractionMode.Failed);
    }
    internal Task ExtractToAsync(string target, bool smart, bool selectedOnly, bool openFolder = false)
        => ExtractCapturedAsync(target, smart, selectedOnly ? SelectedIds() : null, openFolder);
    private Task ExtractCapturedAsync(string target, bool smart, long[]? selectedIds, bool openFolder, bool exactIds = false)
    {
        if (archive is null || OpenedPath is null || IsBusy) return Task.CompletedTask;
        var snapshot = archive.Scan; string path = OpenedPath;
        var index = archive.Index;
        if (selectedIds is { Length: 0 }) return Task.CompletedTask;
        LastExtraction = null;
        if (snapshot.Entries.Count == 0) { StatusText.Text = Text["ClientArchiveEmpty"]; return Task.CompletedTask; }
        return RunAsync(Text["Extracting"], async token =>
        {
            int taskGeneration = generation;
            long[]? expanded = selectedIds is null || exactIds ? selectedIds : await Task.Run(() =>
            {
                var ids = selectedIds.ToHashSet();
                return ArchiveSelection.Expand(index.Entries.Where(e => ids.Contains(e.Id)), snapshot.Entries, token);
            }, token);
            using var report = new WindowProgress<ExtractionProgress>(Dispatcher, p =>
            {
                if (closed || !IsBusy || generation != taskGeneration || token.IsCancellationRequested) return;
                StatusText.Text = Text.Format("ExtractionProgress", p.Completed, p.Skipped, p.Bytes / 1048576.0);
            });
            var result = await WithPasswordAsync(secret => Task.Run(() => worker.ExtractAsync(path,
                new ExtractionOptions(target, smart, SelectedIds: expanded, ExpectedIdentity: snapshot.Identity, NameCodePage: snapshot.NameCodePage),
                ConflictOverride ?? AskConflictAsync, report, token, secret), token), token);
            // A cancelled extraction still has a meaningful partial result and completed files.
            LastExtraction = result;
            // Smart extraction can reserve or rename a root. Prompt for a destination
            // on retry instead of guessing how a path in the archive maps to that root.
            retryDestination = smart ? null : target;
            if (result.Failures.Any(f => f.Code is "PasswordRequired" or "WrongPasswordOrDamaged")) password = null;
            StatusText.Text = ExtractionSummary(result);
            return () => { if (openFolder && result.Completed > 0 && !result.Cancelled && result.Failures.Count == 0) LaunchFolder(result.Destination); };
        }, false);
    }
    private void AddFailureDetails(ActionDialog dialog, IReadOnlyList<ExtractionFailure> items)
    {
        var failures = new ListBox { ItemsSource = items, Height = 140, Background = Background, Foreground = Foreground,
            ItemTemplate = (DataTemplate)FindResource("ExtractionFailureTemplate") };
        var reason = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        failures.SelectionChanged += (_, _) =>
        {
            if (failures.SelectedItem is ExtractionFailure failure)
                reason.Text = string.IsNullOrEmpty(failure.Path) ? Text[failure.Code] : failure.Path + Environment.NewLine + Text[failure.Code];
        };
        failures.SelectedIndex = 0;
        dialog.Body.Children.Add(failures); dialog.Body.Children.Add(reason);
    }

    private void LaunchFolder(string folder)
    {
        try { OpenFolder(folder); }
        catch (Exception error) when (error is Win32Exception or IOException) { StatusText.Text = Text["ExternalOpenFailed"]; }
    }
    private async void OpenFile_Click(object sender, RoutedEventArgs e) => await ActivateAsync();
    internal Task OpenExternalAsync(long id)
    {
        if (archive?.Scan.Identity is null || OpenedPath is null) return Task.CompletedTask;
        var snapshot = archive.Scan; string path = OpenedPath;
        var entry = id >= 0 && id < snapshot.Entries.Count && snapshot.Entries[(int)id].Id == id
            ? snapshot.Entries[(int)id] : snapshot.Entries.SingleOrDefault(e => e.Id == id);
        if (entry is null || entry.IsDirectory) return Task.CompletedTask;
        var app = (ClientApp)Application.Current;
        return app.OpenEntryOnceAsync(this, snapshot.Identity, id, entry.Path, () =>
        {
            if (IsBusy || closingCheck || closed || !app.CanStart(this)) return Task.CompletedTask;
            if (ExternalOpenPolicy.RequiresConfirmation(entry.Path) && !Confirm("ConfirmExecutable", Text.Format("ExecutableHint", Path.GetFileName(entry.Path)), "OpenFile")) return Task.CompletedTask;
            return RunAsync(Text["PreparingExternal"], async token =>
            {
                var record = await WithPasswordAsync(secret => Task.Run(() =>
                    Copies.PrepareForOpenAsync(worker, path, snapshot.Identity, entry, snapshot.NameCodePage, secret, token), token), token);
                return await PrepareCopyLaunchAsync(record, token);
            }, false);
        });
    }

    private async Task<Action> PrepareCopyLaunchAsync(WorkCopyRecord record, CancellationToken token)
    {
        var state = await Task.Run(() => Copies.InspectAsync(record, token), token);
        if (state.State is WorkCopyState.Incomplete or WorkCopyState.Unavailable) throw new ArchiveOperationException("CopyUnavailable");
        token.ThrowIfCancellationRequested();
        if (record.Damage is { } damage)
        {
            string hint = Text.Format("ClientDamagedOpenHint", record.EntryPath, Text[damage.Reason], DamageSize(damage));
            if (!Confirm("ClientDamagedCopy", hint, "ClientOpenAvailable", "ClientKeepCopy"))
                return () => StatusText.Text = Text["ClientUnverifiedKept"];
            await Task.Run(() => Copies.RecheckApprovedContentAsync(state, token), token);
        }
        await Task.Run(() => Copies.RestoreSourceMarkAsync(record, token), token);
        return () =>
        {
            try
            {
                LaunchFile(state.FilePath, new WindowInteropHelper(this).Handle);
                openedCopies[record.Id] = state; LastCopyPath = state.FilePath;
                StatusText.Text = Text[record.Damage is not null ? "ClientUnverifiedOpened" :
                    state.State == WorkCopyState.Modified ? "CopyStateModified" : "ExternalCopyNotice"];
            }
            catch (OperationCanceledException) { StatusText.Text = Text["ClientExternalCancelled"]; }
            catch (Exception error) when (error is Win32Exception or IOException or System.Runtime.InteropServices.COMException)
            { StatusText.Text = Text["ExternalOpenFailed"]; }
        };
    }

    internal static string DamageSize(UnverifiedContent damage) => damage.ExpectedBytes is ulong expected ?
        ClientApp.Text.Format("ClientDecodedOf", damage.DecodedBytes, expected) : ClientApp.Text.Format("ClientDecodedBytes", damage.DecodedBytes);

    private async void Copies_Click(object sender, RoutedEventArgs e) => await ShowCopiesAsync();
    internal async Task ShowCopiesAsync(int pageSize = 2000)
    {
        if (IsBusy) return;
        using var lifetime = new CancellationTokenSource();
        await using var pages = Copies.ReadPagesAsync(pageSize, lifetime.Token).GetAsyncEnumerator();
        Task<WorkCopyListing> ReadPage() => Task.Run(async () =>
            await pages.MoveNextAsync() ? pages.Current : new WorkCopyListing([], false), lifetime.Token);
        WorkCopyListing? listing = null;
        await RunAsync(Text["CheckingCopy"], async token =>
        {
            using var stop = token.Register(lifetime.Cancel);
            var loaded = await ReadPage();
            return () => listing = loaded;
        });
        if (closed || listing is null) return;
        if (listing.Records.Count == 0 && !listing.HasMore) { StatusText.Text = Text["NoWorkCopies"]; return; }
        var dialog = new WorkCopiesDialog(this, Copies, listing, ReadPage, LaunchFolder);
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { lifetime.Cancel(); await dialog.PendingLoad; }
        if (!accepted || dialog.Selected is not { } record) return;
        if (dialog.Choice == CopyAction.Open)
        {
            string file = Copies.FilePath(record);
            if (ExternalOpenPolicy.RequiresConfirmation(file) && !Confirm("ConfirmExecutable", Text.Format("ExecutableHint", Path.GetFileName(file)), "OpenFile")) return;
            await RunAsync(Text["PreparingExternal"], token => PrepareCopyLaunchAsync(record, token), false);
        }
        else if (dialog.Choice == CopyAction.Clean)
        {
            var confirm = new ActionDialog(this, "CleanUnchangedCopy", "Clean"); confirm.Explain(Text["CleanupCopyHint"]);
            var editorsClosed = new CheckBox { Content = Text["EditorsClosed"] }; confirm.Body.Children.Add(editorsClosed);
            confirm.Accept.IsEnabled = false; editorsClosed.Click += (_, _) => confirm.Accept.IsEnabled = editorsClosed.IsChecked == true;
            if (confirm.ShowDialog() == true) await CleanupCopyAsync(record);
        }
        else
        {
            var picker = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(record.EntryPath), Filter = Text["ClientAllFilesFilter"], OverwritePrompt = false };
            if (picker.ShowDialog(this) != true) return;
            string destination = picker.FileName;
            bool overwrite = File.Exists(destination);
            if (overwrite && !Confirm("ClientReplaceExport", destination, "Replace")) return;
            await ExportCopyAsync(record, destination, overwrite);
        }
    }

    internal Task ExportCopyAsync(WorkCopyRecord record, string destination, bool overwrite = false) => RunAsync(Text["SaveCopyAs"], async token =>
    {
        await Task.Run(() => Copies.ExportAsync(record, destination, overwrite, token), token);
        return () => StatusText.Text = Text.Format("CopyExported", destination);
    }, false);
}
