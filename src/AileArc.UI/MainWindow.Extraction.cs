using AileArc.Core;
using AileArc.Core.Extraction;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AileArc.UI;

public sealed partial class MainWindow
{
    private bool dialogOpen;
    private bool allowClose;
    private TaskCompletionSource idleCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<string?> AskPasswordAsync(string reason)
    {
        if (closed || dialogOpen) return null;
        var password = new PasswordBox { Header = text["Password"], MinWidth = 320 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = text[reason], TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        content.Children.Add(password);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["Password"], Content = content,
            PrimaryButtonText = text["Continue"], CloseButtonText = text["Cancel"], DefaultButton = ContentDialogButton.Primary };
        dialogOpen = true;
        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? password.Password : null;
        }
        finally { password.Password = ""; dialogOpen = false; }
    }

    private async Task ExtractAsync(bool smart, bool selectedOnly = false)
    {
        if (index is null || operation is not null || dialogOpen) return;
        var snapshot = scan!;
        var selection = list.SelectedItems.Cast<DisplayEntry>().Select(e => e.Entry).ToArray();
        if (selectedOnly && selection.Length == 0) return;
        long[]? selectedIds = selectedOnly ? await Task.Run(() => ArchiveSelection.Expand(selection, snapshot.Entries)) : null;
        if (selectedOnly && selectedIds!.Length == 0) return;
        var destination = new TextBox { Header = text["Destination"], Text = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(archivePath)), MinWidth = 420 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(destination);
        var browse = Button(text["BrowseFolder"], async () =>
        {
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null) destination.Text = folder.Path;
            }
            catch (Exception) { notice.Text = text["FolderPickerFailed"]; }
        });
        content.Children.Add(browse);
        content.Children.Add(new TextBlock { Text = selectedOnly ? text.Format("SelectedExtractHint", selectedIds!.Length) : text[smart ? "SmartExtractHint" : "ExtractHint"], TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text[selectedOnly ? "ExtractSelected" : smart ? "SmartExtract" : "ExtractTo"], Content = content,
            PrimaryButtonText = text["Extract"], CloseButtonText = text["Cancel"] };
        dialogOpen = true;
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
        if (choice != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(destination.Text)) return;
        string targetDirectory = destination.Text.Trim();
        var current = new CancellationTokenSource();
        operation = current;
        SetBusy(true);
        status.Text = text["Extracting"];
        SetFailures([]);
        try
        {
            long lastProgress = 0;
            var report = new Progress<ExtractionProgress>(p =>
            {
                if (closed || current.IsCancellationRequested || !ReferenceEquals(operation, current)) return;
                long now = Environment.TickCount64;
                if (now - lastProgress < 100) return;
                lastProgress = now;
                status.Text = text.Format("ExtractionProgress", p.Completed, p.Skipped, p.Bytes / (1024.0 * 1024));
            });
            ExtractionResult result;
            while (true)
            {
                try
                {
                    result = await Task.Run(() => client.ExtractAsync(archivePath, new ExtractionOptions(targetDirectory, smart, SelectedIds: selectedIds, ExpectedIdentity: snapshot.Identity),
                        RequestConflictAsync, report, current.Token, sessionPassword), current.Token);
                    break;
                }
                catch (ArchiveOperationException error) when (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
                {
                    string? entered = await AskPasswordAsync(error.Code);
                    if (entered is null) { current.Cancel(); throw new OperationCanceledException(current.Token); }
                    sessionPassword = entered;
                }
            }
            if (closed) return;
            status.Text = text.Format(result.Cancelled ? "ExtractionCancelled" : result.Failures.Count > 0 ? "ExtractionPartial" : "ExtractionDone",
                result.Completed, result.Skipped, result.Destination);
            if (result.Failures.Count > 0)
            {
                SetFailures(result.Failures);
                notice.Text = text.Format("FailureCount", result.Failures.Count);
            }
            else notice.Text = text["ReadOnlyNotice"];
        }
        catch (OperationCanceledException) { if (!closed) status.Text = text["Cancelled"]; }
        catch (ArchiveOperationException error) { if (!closed) ShowFailure(error.Code); }
        catch (Exception) { if (!closed) ShowFailure("WriteFailed"); }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!closed) SetBusy(false);
            current.Dispose();
        }
    }

    private Task<ConflictDecision> RequestConflictAsync(string path, CancellationToken token)
    {
        var completion = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            if (closed || token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
            var choices = new ComboBox { MinWidth = 320, SelectedIndex = 0 };
            choices.Items.Add(text["RenameAutomatically"]);
            choices.Items.Add(text["Skip"]);
            choices.Items.Add(text["Replace"]);
            choices.SelectedIndex = 0;
            var applyAll = new CheckBox { Content = text["ApplyToAll"] };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
            content.Children.Add(choices);
            content.Children.Add(applyAll);
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["Conflict"], Content = content,
                PrimaryButtonText = text["Continue"], CloseButtonText = text["Cancel"] };
            dialogOpen = true;
            using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            try
            {
                var result = await dialog.ShowAsync();
                var policy = choices.SelectedIndex switch { 1 => ConflictChoice.Skip, 2 => ConflictChoice.Replace, _ => ConflictChoice.Rename };
                completion.TrySetResult(new(result == ContentDialogResult.Primary ? policy : ConflictChoice.Cancel, applyAll.IsChecked == true));
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { dialogOpen = false; }
        })) completion.TrySetCanceled(token);
        return completion.Task;
    }

    private async Task ConfirmCloseAsync(bool restartApplication = false)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            if (operation is not null)
            {
                var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["TaskRunning"], Content = text["CloseTaskHint"],
                    PrimaryButtonText = text["CancelAndExit"], CloseButtonText = text["KeepWaiting"] };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                var waiting = idleCompletion.Task;
                operation?.Cancel();
                await waiting;
            }
            var copies = openedCopies.Values.ToArray();
            var states = await Task.Run(async () =>
            {
                var result = new List<AileArc.Core.WorkCopies.WorkCopyStatus>();
                foreach (var record in copies) result.Add(await workCopyStore.InspectAsync(record));
                return result;
            });
            if (states.Any(s => s.State != AileArc.Core.WorkCopies.WorkCopyState.Unchanged))
            {
                var warning = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["WorkCopies"], Content = text["CloseCopiesHint"],
                    PrimaryButtonText = text["KeepCopiesAndExit"], CloseButtonText = text["Return"] };
                if (await warning.ShowAsync() != ContentDialogResult.Primary) return;
            }
            if (restartApplication)
            {
                var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                if (archivePath.Length > 0) start.ArgumentList.Add(archivePath);
                System.Diagnostics.Process.Start(start);
            }
            allowClose = true;
            Close();
        }
        catch (Exception) { if (!closed) ShowFailure("OpenFailed"); }
        finally { dialogOpen = false; }
    }
}
