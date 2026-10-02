using AileArc.Core.Lifecycle;
using Microsoft.UI.Xaml;

namespace AileArc.UI;

public sealed partial class MainWindow
{
    private bool shellActionRunning;

    public async Task RunShellActionAsync(ActivationAction action, string[] paths, bool openFirst = false)
    {
        if (closed) return;
        if (shellActionRunning || IsBusy || dialogOpen) { notice.Text = text["TaskRunning"]; return; }
        shellActionRunning = true;
        try
        {
            // A newly activated window may not yet have a XamlRoot for its dialog.
            if (root.XamlRoot is null)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Loaded(object sender, RoutedEventArgs args) => ready.TrySetResult();
                void Closed(object sender, WindowEventArgs args) => ready.TrySetResult();
                root.Loaded += Loaded;
                this.Closed += Closed;
                try { await ready.Task; }
                finally { root.Loaded -= Loaded; this.Closed -= Closed; }
            }
            if (closed) return;
            if (action == ActivationAction.Create) { await CreateArchiveAsync(paths); return; }
            if (openFirst) await OpenArchiveAsync(paths[0]);
            if (closed || index is null) return;
            await ExtractAsync(action == ActivationAction.SmartExtract, useArchiveDirectory: action == ActivationAction.SmartExtract);
        }
        catch (Exception) { if (!closed) ShowFailure("OpenFailed"); }
        finally { shellActionRunning = false; }
    }
}
