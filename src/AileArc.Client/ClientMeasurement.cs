using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AileArc.Core;

namespace AileArc.Client;

// Opt-in companion to Measure-Client.ps1. Use real window, controls and worker
// paths, while keeping the measurement out of the user's foreground session.
internal static class ClientMeasurement
{
    internal static int Run(string path, string mode, long launchTick, int scrollSeconds = 0)
    {
        if (Console.ReadLine() != "start") return 2;
        ClientApp.Text = new(LanguageService.DefaultLanguage);
        var app = new ClientApp { ShutdownMode = ShutdownMode.OnExplicitShutdown, HideTestWindows = true };
        var window = app.CreateWindow();
        window.ShowActivated = false;
        window.Opacity = 1;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - window.Width - 64;
        window.Top = SystemParameters.VirtualScreenTop;
        int exitCode = 1;
        double Elapsed() => Stopwatch.GetElapsedTime(launchTick).TotalMilliseconds;
        app.Startup += async (_, _) =>
        {
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            double? scanStartMs = null, indexStartMs = null, boundMs = null, frameMs = null, cancelAtMs = null, cancelMs = null;
            int? entriesAtCancel = null;
            bool passwordRequired = false;
            long cancelTick = 0;
            var grid = window.TestGrid;
            var bound = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
            void Bound(object? sender, EventArgs e) { if (grid.Items.Count > 0) boundMs ??= Elapsed(); }
            void Rendering(object? sender, EventArgs e)
            {
                if (!passwordRequired && window.OwnedWindows.OfType<PasswordDialog>().FirstOrDefault() is { IsVisible: true } prompt)
                {
                    passwordRequired = true;
                    window.Dispatcher.BeginInvoke(new Action(() => { if (prompt.IsVisible) prompt.Close(); }));
                }
                if (frameMs is not null || grid.Items.Count == 0 ||
                    grid.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow { IsVisible: true, ActualHeight: > 0, ActualWidth: > 0 } row ||
                    row.Item != grid.Items[0] || PresentationSource.FromVisual(row) is null || row.Item is not EntryView entry ||
                    window.NameColumn.GetCellContent(row) is not { } cell || !ContainsName(cell, entry.Name)) return;
                frameMs = Elapsed(); frame.TrySetResult();
            }
            var status = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            var label = ClientApp.Text["ScanProgress"].Split("{0:N0}", StringSplitOptions.None);
            void OnProgress(object? sender, EventArgs e)
            {
                if (window.StatusText.Text == ClientApp.Text["Scanning"]) scanStartMs ??= Elapsed();
                if (window.StatusText.Text == ClientApp.Text["BuildingIndex"]) indexStartMs ??= Elapsed();
                string displayed = window.StatusText.Text;
                if (mode != "cancel" || cancelTick != 0 || !window.IsBusy || !window.CancelButton.IsVisible || !window.CancelButton.IsEnabled ||
                    label.Length != 2 || !displayed.StartsWith(label[0], StringComparison.Ordinal) || !displayed.EndsWith(label[1], StringComparison.Ordinal) ||
                    !int.TryParse(displayed.AsSpan(label[0].Length, displayed.Length - label[0].Length - label[1].Length),
                        NumberStyles.Number, CultureInfo.CurrentCulture, out int count) || count <= 0) return;
                entriesAtCancel = count;
                cancelAtMs = Elapsed();
                cancelTick = Stopwatch.GetTimestamp();
                window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            bound.AddValueChanged(grid, Bound); status.AddValueChanged(window.StatusText, OnProgress);
            CompositionTarget.Rendering += Rendering;
            try
            {
                await app.OpenPathAsync(path, preferred: window);
                if (passwordRequired) throw new InvalidOperationException("Use an unencrypted measurement fixture.");
                if (mode == "cancel")
                {
                    if (cancelTick == 0 || window.IsBusy || window.OpenedPath is not null || window.StatusText.Text != ClientApp.Text["Cancelled"])
                        throw new InvalidOperationException("The scan did not reach a cancellable progress notification.");
                    cancelMs = Stopwatch.GetElapsedTime(cancelTick).TotalMilliseconds;
                }
                else
                {
                    if (window.OpenedPath != Path.GetFullPath(path) || boundMs is null || scanStartMs is null || indexStartMs is null)
                        throw new InvalidOperationException("Archive did not bind a nonempty page.");
                    await frame.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                }
                var scroll = mode == "scroll" ? await ExerciseScrollingAsync(window, scrollSeconds) : null;
                Console.WriteLine(JsonSerializer.Serialize(new { mode, scanStartMs, indexStartMs, boundMs, frameMs, cancelAtMs, cancelMs, entriesAtCancel, scroll,
                    pageRows = grid.Items.Count, dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX, width = window.ActualWidth, height = window.ActualHeight }));
                exitCode = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); }
            finally
            {
                CompositionTarget.Rendering -= Rendering; status.RemoveValueChanged(window.StatusText, OnProgress); bound.RemoveValueChanged(grid, Bound);
                window.ApproveClose(); window.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(); return exitCode;
    }

    private sealed record ScrollResult(double Seconds, int Movements, int PageChanges, int PeakRealizedRows, long ManagedStartBytes, long ManagedEndBytes);
    private static async Task<ScrollResult> ExerciseScrollingAsync(MainWindow window, int seconds)
    {
        var grid = window.TestGrid;
        if (grid.Items.Count < 100 || !window.NextButton.IsEnabled) throw new InvalidOperationException("Use a paged measurement fixture.");
        long initial = GC.GetTotalMemory(forceFullCollection: true);
        var clock = Stopwatch.StartNew();
        int movements = 0, pages = 0, peakRows = 0;
        bool forward = true;
        while (clock.Elapsed.TotalSeconds < seconds || pages == 0)
        {
            if (movements > 0 && movements % 24 == 0)
            {
                if (!window.NextButton.IsEnabled) forward = false;
                if (!window.PreviousButton.IsEnabled) forward = true;
                var button = forward ? window.NextButton : window.PreviousButton;
                string previousId = ((EntryView)grid.Items[0]).Row.Id;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (window.IsBusy) await Task.Delay(5);
                if (grid.Items.Count == 0 || grid.SelectedItems.Count != 0 || ((EntryView)grid.Items[0]).Row.Id == previousId)
                    throw new InvalidOperationException("Paging failed to commit a new page and clear its old selection.");
                pages++;
            }
            if (movements % 24 == 0) grid.SelectedIndex = grid.Items.Count / 2;
            var expectedSelection = grid.Items[grid.Items.Count / 2];
            int nearEnd = (movements * 7) % Math.Max(1, grid.Items.Count / 4);
            int index = movements % 2 == 0 ? grid.Items.Count - 1 - nearEnd : nearEnd;
            var item = (EntryView)grid.Items[index];
            grid.ScrollIntoView(item, window.NameColumn);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row || row.Item != item ||
                window.NameColumn.GetCellContent(row) is not { } cell || !ContainsName(cell, item.Name) ||
                grid.SelectedItems.Count != 1 || grid.SelectedItem != expectedSelection)
                throw new InvalidOperationException("Recycled row or selection does not match the requested entry.");
            int realized = VisualTreeHelper.GetChildrenCount(VisualTreeHelper.GetParent(row));
            if (realized <= 0 || realized > Math.Ceiling(grid.ActualHeight / grid.RowHeight) * 3 + 8)
                throw new InvalidOperationException("Realized rows exceed the virtualized viewport.");
            peakRows = Math.Max(peakRows, realized); movements++;
            await Task.Delay(35);
        }
        return new(clock.Elapsed.TotalSeconds, movements, pages, peakRows, initial, GC.GetTotalMemory(forceFullCollection: true));
    }

    private static bool ContainsName(DependencyObject element, string name)
    {
        if (element is TextBlock { IsVisible: true, ActualWidth: > 0 } text && text.Text == name) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (ContainsName(VisualTreeHelper.GetChild(element, i), name)) return true;
        return false;
    }
}
