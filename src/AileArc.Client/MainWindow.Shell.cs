using System.IO;
using System.Windows;
using AileArc.Core;
using AileArc.Core.Lifecycle;

namespace AileArc.Client;

public partial class MainWindow
{
    internal bool ShellActionPending { get; private set; }
    internal void QueueShellAction(ActivationAction action, string[] paths)
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
                    if (action == ActivationAction.SmartExtract) await ExtractToAsync(Path.GetDirectoryName(OpenedPath)!, true, false);
                    else if (action == ActivationAction.ExtractTo) await ShowExtractDialogAsync();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ArchiveOperationException)
            { if (!closed) StatusText.Text = Text["OpenFailed"]; }
            finally { ShellActionPending = false; }
        }));
    }
}
