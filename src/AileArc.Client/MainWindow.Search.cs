using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AileArc.Client;

public partial class MainWindow
{
    private CancellationTokenSource? pendingSearch;
    internal Task SearchSettled { get; private set; } = Task.CompletedTask;

    private void InitializeSearch()
    {
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, e) =>
        {
            SearchBox.Focus(); if (!composing) SearchBox.SelectAll(); e.Handled = true;
        }, (_, e) => { e.CanExecute = SearchBox.IsEnabled; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        void Begin(object sender, TextCompositionEventArgs e) { composing = true; CancelLiveSearch(); }
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(Begin), true);
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(Begin), true);
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler((_, _) => EndComposition()), true);
        SearchBox.LostKeyboardFocus += (_, _) => EndComposition();
    }
    private void EndComposition()
    {
        if (!composing) return;
        composing = false;
        // TextBox may already contain the final text, or insert it after this preview event.
        ScheduleSearch();
    }
    private void CancelLiveSearch()
    {
        var previous = pendingSearch; pendingSearch = null; previous?.Cancel();
    }
    private void ScheduleSearch(bool immediate = false)
    {
        CancelLiveSearch();
        if (archive is null || composing || closed || IsBusy || !SearchBox.IsEnabled) return;
        if (SearchBox.Text == view.Search && (LocalSearchBox.IsChecked == true) == view.Local) return;
        var request = pendingSearch = new CancellationTokenSource();
        SearchSettled = RefreshAsync();
        async Task RefreshAsync()
        {
            try
            {
                if (!immediate) await Task.Delay(180, request.Token);
                request.Token.ThrowIfCancellationRequested();
                await QueryAsync(directory, 0, submit: true, live: request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (InvalidOperationException) when (request.IsCancellationRequested) { } // Sort can wrap cancellation.
            finally { if (ReferenceEquals(request, pendingSearch)) pendingSearch = null; request.Dispose(); }
        }
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ClearSearch is null) return;
        bool empty = SearchBox.Text.Length == 0;
        SearchHint.Visibility = ShortcutHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ScheduleSearch();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus(); SearchBox.Clear(); ScheduleSearch(immediate: true);
    }
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (!composing && e.Key == Key.Enter) { e.Handled = true; ScheduleSearch(immediate: true); }
    }
}
