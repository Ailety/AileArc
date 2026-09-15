using System.Diagnostics;
using AileArc.Core;
using AileArc.Core.Extraction;
using AileArc.Core.WorkCopies;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AileArc.UI;

public sealed partial class MainWindow
{
    private readonly WorkCopyStore workCopyStore = new();
    private readonly Dictionary<string, WorkCopyRecord> openedCopies = [];
    private readonly DispatcherTimer workCopyTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private WorkCopyRecord? latestCopy;
    private bool checkingCopy;

    private void InitializeWorkCopyMonitoring()
    {
        workCopyTimer.Tick += async (_, _) =>
        {
            if (closed || checkingCopy || dialogOpen || operation is not null || latestCopy is null) return;
            checkingCopy = true;
            var current = latestCopy;
            try
            {
                var state = await Task.Run(() => workCopyStore.InspectAsync(current));
                if (closed || !ReferenceEquals(current, latestCopy)) return;
                if (state.State == WorkCopyState.Modified) notice.Text = text.Format("ModifiedCopyNotice", System.IO.Path.GetFileName(current.EntryPath));
                else if (state.State == WorkCopyState.Unavailable) notice.Text = text["CopyUnavailable"];
            }
            catch (Exception) { /* A transient sharing violation never discards a working copy. */ }
            finally { checkingCopy = false; }
        };
        workCopyTimer.Start();
    }

    private async Task<bool> ConfirmExternalAsync(string path)
    {
        if (!ExternalOpenPolicy.RequiresConfirmation(path)) return true;
        if (dialogOpen) return false;
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["ConfirmExecutable"],
            Content = text.Format("ExecutableHint", System.IO.Path.GetFileName(path)),
            PrimaryButtonText = text["OpenFile"], CloseButtonText = text["Cancel"] };
        dialogOpen = true;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { dialogOpen = false; }
    }

    private async Task OpenExternalAsync(long entryId)
    {
        var snapshot = scan;
        if (snapshot?.Identity is null || operation is not null || dialogOpen) return;
        var entry = snapshot.Entries.SingleOrDefault(e => e.Id == entryId);
        if (entry is null || entry.IsDirectory) return;
        if (!await ConfirmExternalAsync(entry.Path)) return;
        var current = new CancellationTokenSource();
        operation = current;
        SetBusy(true);
        status.Text = text["PreparingExternal"];
        SetFailures([]);
        try
        {
            WorkCopyRecord record;
            while (true)
            {
                try
                {
                    record = await Task.Run(() => workCopyStore.PrepareAsync(archivePath, snapshot.Identity, entry,
                        (destination, token) => client.ExtractAsync(archivePath,
                            new ExtractionOptions(destination, Smart: false, ByteLimit: WorkCopyStore.MaxFileBytes, SelectedIds: [entry.Id], ExpectedIdentity: snapshot.Identity),
                            cancellationToken: token, password: sessionPassword), current.Token), current.Token);
                    break;
                }
                catch (ArchiveOperationException error) when (error.Code is "PasswordRequired" or "WrongPasswordOrDamaged")
                {
                    string? entered = await AskPasswordAsync(error.Code);
                    if (entered is null) { current.Cancel(); throw new OperationCanceledException(current.Token); }
                    sessionPassword = entered;
                }
            }
            var state = await Task.Run(() => workCopyStore.InspectAsync(record), current.Token);
            if (state.State is WorkCopyState.Unavailable or WorkCopyState.Incomplete) throw new ArchiveOperationException("CopyUnavailable");
            await Task.Run(() => workCopyStore.RestoreSourceMarkAsync(record, current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (closed) return;
            // From this point on, even failure to launch leaves a recoverable file and manifest.
            openedCopies[record.Id] = record;
            latestCopy = record;
            ExternalApplication.Open(state.FilePath, WinRT.Interop.WindowNative.GetWindowHandle(this));
            notice.Text = text["ExternalCopyNotice"];
            status.Text = text.Format("OpenedExternal", System.IO.Path.GetFileName(entry.Path));
        }
        catch (OperationCanceledException) { if (!closed) status.Text = text["Cancelled"]; }
        catch (ArchiveOperationException error) { if (!closed) ShowFailure(error.Code, entry.Path); }
        catch (Exception) { if (!closed) ShowFailure("ExternalOpenFailed", entry.Path); }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!closed) SetBusy(false);
            current.Dispose();
        }
    }

    private async Task ShowWorkCopiesAsync()
    {
        if (dialogOpen || operation is not null) return;
        dialogOpen = true;
        try
        {
            var records = await Task.Run(() => workCopyStore.LoadAsync());
            if (records.Count == 0)
            {
                var emptyDialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["WorkCopies"],
                    Content = text["NoWorkCopies"], CloseButtonText = text["Close"] };
                await emptyDialog.ShowAsync();
                return;
            }
            var selector = new ComboBox { MinWidth = 460, MaxWidth = 520, ItemsSource = records.Select(r => $"{r.CreatedUtc.ToLocalTime():g} · {r.EntryPath}").ToArray() };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selector, text["WorkCopies"]);
            var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, IsTextSelectionEnabled = true };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = text["CopiesRetainedHint"], TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
            content.Children.Add(selector);
            content.Children.Add(description);
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["WorkCopies"], Content = content,
                PrimaryButtonText = text["SaveCopyAs"], SecondaryButtonText = text["OpenCopyFolder"], CloseButtonText = text["Close"] };
            selector.SelectionChanged += async (_, _) =>
            {
                int selected = selector.SelectedIndex;
                if (selected < 0) return;
                dialog.IsPrimaryButtonEnabled = false;
                description.Text = text["CheckingCopy"];
                try
                {
                    var state = await Task.Run(() => workCopyStore.InspectAsync(records[selected]));
                    if (selector.SelectedIndex != selected) return;
                    description.Text = $"{text["CopyState" + state.State]}\n{state.FilePath}";
                    dialog.IsPrimaryButtonEnabled = state.State is WorkCopyState.Modified or WorkCopyState.Unchanged;
                }
                catch (Exception) { description.Text = text["CopyUnavailable"]; }
            };
            selector.SelectedIndex = 0;
            var choice = await dialog.ShowAsync();
            var record = records[selector.SelectedIndex];
            if (choice == ContentDialogResult.Primary)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id) { SuggestedFileName = System.IO.Path.GetFileName(record.EntryPath) };
                picker.FileTypeChoices.Add(text["AllFiles"], new List<string> { "*" });
                var result = await picker.PickSaveFileAsync();
                if (result is not null)
                {
                    dialogOpen = false;
                    await ExportCopyAsync(record, result.Path);
                }
            }
            else if (choice == ContentDialogResult.Secondary)
            {
                string folder = System.IO.Path.GetDirectoryName(workCopyStore.FilePath(record))!;
                using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
        }
        catch (ArchiveOperationException error) { if (!closed) ShowFailure(error.Code); }
        catch (Exception) { if (!closed) ShowFailure("CopyUnavailable"); }
        finally { dialogOpen = false; }
    }

    private async Task ExportCopyAsync(WorkCopyRecord record, string destination)
    {
        var current = new CancellationTokenSource();
        operation = current;
        SetBusy(true);
        try
        {
            await Task.Run(() => workCopyStore.ExportAsync(record, destination, current.Token), current.Token);
            if (!closed) status.Text = text.Format("CopyExported", destination);
        }
        catch (OperationCanceledException) { if (!closed) status.Text = text["Cancelled"]; }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!closed) SetBusy(false);
            current.Dispose();
        }
    }
}
