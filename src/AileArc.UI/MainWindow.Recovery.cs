using AileArc.Core;
using AileArc.Core.Recovery;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AileArc.UI;

public sealed partial class MainWindow
{
    private async Task RefreshRecoveryBadgeAsync()
    {
        try
        {
            var items = await Task.Run(() => RecoveryStore.Default.List());
            if (!closed) recovery.Content = items.Any(i => i.State != RecoveryState.Busy)
                ? text.Format("RecoveryCount", items.Count(i => i.State != RecoveryState.Busy)) : text["Recovery"];
        }
        catch (Exception) { /* Recovery failures never prevent normal archive browsing. */ }
    }
    private async Task ShowRecoveryAsync()
    {
        if (dialogOpen || operation is not null) return;
        dialogOpen = true;
        try
        {
            var items = await Task.Run(() => RecoveryStore.Default.List());
            if (items.Count == 0)
            {
                await new ContentDialog { XamlRoot = root.XamlRoot, Title = text["Recovery"], Content = text["NoRecoveryItems"], CloseButtonText = text["Close"] }.ShowAsync();
                return;
            }
            var selection = new ComboBox { MinWidth = 460, MaxWidth = 520, ItemsSource = items.Select(i => $"{i.Record.CreatedUtc.ToLocalTime():g} · {text["RecoveryKind" + i.Record.Kind]}").ToArray() };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selection, text["Recovery"]);
            var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, IsTextSelectionEnabled = true };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = text["RecoveryHint"], TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
            content.Children.Add(selection); content.Children.Add(description);
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["Recovery"], Content = content,
                PrimaryButtonText = text["Clean"], CloseButtonText = text["Close"] };
            selection.SelectionChanged += (_, _) =>
            {
                if (selection.SelectedIndex < 0) return;
                var item = items[selection.SelectedIndex];
                description.Text = $"{text["RecoveryState" + item.State]}\n{item.Record.Path}\n{item.Bytes:N0} B";
                dialog.IsPrimaryButtonEnabled = item.State is RecoveryState.Recoverable or RecoveryState.Missing;
            };
            selection.SelectedIndex = 0;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = items[selection.SelectedIndex];
            var confirm = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["Recovery"], Content = text["RecoveryConfirm"] + "\n" + selected.Record.Path,
                PrimaryButtonText = text["Clean"], CloseButtonText = text["Cancel"] };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            dialogOpen = false;
            using var current = new CancellationTokenSource();
            operation = current;
            SetBusy(true);
            try
            {
                var state = await Task.Run(() => RecoveryStore.Default.Cleanup(selected.Record.Id, current.Token), current.Token);
                if (!closed) status.Text = text[state is RecoveryState.Recoverable or RecoveryState.Missing ? "RecoveryCleaned" : "RecoveryCleanupRefused"];
            }
            finally
            {
                if (ReferenceEquals(operation, current)) operation = null;
                if (!closed) SetBusy(false);
            }
        }
        catch (OperationCanceledException) { if (!closed) status.Text = text["Cancelled"]; }
        catch (ArchiveOperationException error) { if (!closed) ShowFailure(error.Code); }
        catch (Exception) { if (!closed) ShowFailure("RecoveryCleanupRefused"); }
        finally { dialogOpen = false; await RefreshRecoveryBadgeAsync(); }
    }
}
