using System.Windows.Controls;
using System.Windows.Input;

namespace AileArc.Client;

public partial class MainWindow
{
    private int? rangeAnchor;
    private HashSet<EntryView>? additiveRangeBase;
    private EntryView? keyboardCursor;
    private bool revealQueued;

    private void ResetKeyboardRange() { rangeAnchor = null; additiveRangeBase = null; keyboardCursor = null; }

    private void RevealCursorAfterResize()
    {
        if (keyboardCursor is null || revealQueued) return;
        revealQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            revealQueued = false;
            if (!closed && keyboardCursor is { } row && EntryGrid.CurrentItem == row) EntryGrid.ScrollIntoView(row, NameColumn);
        }));
    }

    private bool HandleListKey(Key key, ModifierKeys modifiers, EntryView? focused)
    {
        if (IsBusy || displayed.Length == 0 || (modifiers & ~(ModifierKeys.Shift | ModifierKeys.Control)) != 0) return false;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool toggle = key == Key.Space && !shift;
        if (!toggle && key is not (Key.Up or Key.Down or Key.Home or Key.End)) return false;
        int here = EntryGrid.Items.IndexOf(focused ?? EntryGrid.CurrentItem);
        if (here < 0) here = EntryGrid.SelectedIndex;
        int last = displayed.Length - 1;
        int next = key switch
        {
            Key.Up => Math.Max(0, here - 1),
            Key.Down => Math.Min(last, here + 1),
            Key.Home => 0,
            Key.End => last,
            _ => Math.Max(0, here)
        };
        var row = displayed[next];
        var selected = EntryGrid.SelectedItems.Cast<EntryView>().ToHashSet();
        if (shift)
        {
            rangeAnchor ??= Math.Max(0, here);
            if (ctrl) additiveRangeBase ??= new(selected);
            else additiveRangeBase = null;
            selected.Clear();
            if (additiveRangeBase is not null) selected.UnionWith(additiveRangeBase);
            for (int i = Math.Min(rangeAnchor.Value, next); i <= Math.Max(rangeAnchor.Value, next); i++) selected.Add(displayed[i]);
        }
        else
        {
            rangeAnchor = next; additiveRangeBase = null;
            if (toggle) { if (!selected.Remove(row)) selected.Add(row); }
            else if (!ctrl) { selected.Clear(); selected.Add(row); }
        }

        changingItems = true;
        try
        {
            EntryGrid.ScrollIntoView(row, NameColumn);
            EntryGrid.CurrentCell = new DataGridCellInfo(row, NameColumn);
            var actual = EntryGrid.SelectedItems.Cast<EntryView>().ToHashSet();
            foreach (var old in actual.Except(selected)) EntryGrid.SelectedItems.Remove(old);
            foreach (var added in selected.Except(actual)) EntryGrid.SelectedItems.Add(added);
        }
        finally { changingItems = false; }
        keyboardCursor = row;
        UpdateSelection();
        return true;
    }
}
