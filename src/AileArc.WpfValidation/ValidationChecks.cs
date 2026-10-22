using System.Windows;
using System.Windows.Threading;

namespace AileArc.WpfValidation;

// Exercises our actual templates and DataGrid state, not a second selection model.
// No Win32/UI Automation injection; physical input is verified separately.
internal static class ValidationChecks
{
    public static int Run(Application app)
    {
        int result = 1;
        var window = new MainWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                await window.LoadSampleAsync(100000);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var grid = window.ValidationGrid;
                window.ValidationSearch.Text = "中文测试"; window.UpdateLayout();
                var caret = window.ValidationSearch.GetRectFromCharacterIndex(2);
                Require(!caret.IsEmpty && caret.Height >= window.ValidationSearch.FontSize && caret.Top >= 0 && caret.Bottom <= window.ValidationSearch.ActualHeight, "text host does not clip Unicode line");
                Require(grid.Items.Count == 100000, "sample count");
                int firstRows = window.RealizedRowCount();
                Require(firstRows is > 0 and < 200, "virtualization at start");
                grid.SelectedItems.Add(grid.Items[2]); grid.SelectedItems.Add(grid.Items[7]);
                Require(grid.SelectedItems.Count == 2 && window.ValidationAllState is null, "non-contiguous selection / mixed header");
                window.ToggleAll();
                Require(grid.SelectedItems.Count == 100000 && window.ValidationAllState is true, "select all");
                grid.SelectedItems.Remove(grid.Items[7]);
                Require(grid.SelectedItems.Count == 99999 && window.ValidationAllState is null, "exclude from all");
                window.ToggleAll();
                Require(grid.SelectedItems.Count == 100000, "mixed header selects all");
                grid.UnselectAll();
                Require(window.ValidationAllState is false, "clear");
                grid.SelectedItems.Add(grid.Items[99999]); grid.ScrollIntoView(grid.Items[99999]);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Require(grid.ItemContainerGenerator.ContainerFromIndex(99999) is not null && window.RealizedRowCount() < 200, "virtualized last row");
                int lastRows = window.RealizedRowCount();
                await window.LoadSampleAsync(1000);
                Require(grid.Items.Count == 1000 && grid.SelectedItems.Count == 0, "mode replacement clears selection");
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "self-test-result.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { Passed = true, LogicalRows = 100000, RealizedAtStart = firstRows, RealizedAtEnd = lastRows, Checks = 10 }), System.Text.Encoding.UTF8);
                result = 0;
            }
            catch (Exception error) { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"), error.ToString(), System.Text.Encoding.UTF8); }
            finally { window.Close(); app.Shutdown(result); }
        };
        app.Run(window);
        return result;
    }
    private static void Require(bool success, string assertion) { if (!success) throw new InvalidOperationException(assertion); }
}
