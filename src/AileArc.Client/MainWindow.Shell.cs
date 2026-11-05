using System.IO;
using System.Windows;
using AileArc.Core;
using AileArc.Core.Lifecycle;

namespace AileArc.Client;

public partial class MainWindow
{
    internal bool ShellActionPending { get; private set; }
    internal AileArc.Shared.ArchiveIdentity? CurrentIdentity => archive?.Scan.Identity;
    internal async Task<bool> ReloadActivatedPathAsync(string path)
    {
        if (IsBusy || OwnedWindows.Count > 0 || closed) return false;
        var original = archive;
        await OpenAsync(path, codePage, OpenIntent.Refresh);
        return !ReferenceEquals(original, archive);
    }
    internal void QueueShellAction(ActivationAction action, string[] paths, bool reload = false)
    {
        if (closed) return;
        if (ShellActionPending || IsBusy || OwnedWindows.Count > 0 || !((ClientApp)Application.Current).CanStart(this))
        { StatusText.Text = Text["TaskRunning"]; return; }
        ShellActionPending = true;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (closed) return;
                if (action == ActivationAction.Create) await ShowCreateDialogAsync(paths);
                else if (archive is not null && OpenedPath is not null)
                {
                    if (reload && !await ReloadActivatedPathAsync(paths[0])) return;
                    if (action == ActivationAction.SmartExtract) await ExtractToAsync(Path.GetDirectoryName(OpenedPath)!, true, false, ((ClientApp)Application.Current).OpenAfterExtraction);
                    else if (action == ActivationAction.ExtractTo) await ShowExtractDialogAsync();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ArchiveOperationException)
            { if (!closed) StatusText.Text = Text["OpenFailed"]; }
            finally { ShellActionPending = false; }
        }));
    }
}
