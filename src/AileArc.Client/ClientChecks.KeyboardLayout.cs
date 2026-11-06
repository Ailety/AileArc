using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    // Route actual WPF key events without sending global keystrokes or altering the user's IME.
    private sealed class FixtureKeyboard() : KeyboardDevice(InputManager.Current)
    {
        public ModifierKeys Held { get; set; }
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl when Held.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftShift when Held.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            _ => KeyStates.None
        };
    }

    private static async Task CheckKeyboardAndLayoutAsync(ClientApp app, string root, string? outputDirectory)
    {
        string path = Path.Combine(root, "键盘选择.zip");
        if (outputDirectory is not null) Directory.CreateDirectory(outputDirectory);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            for (int i = 0; i < 230; i++)
            {
                var entry = zip.CreateEntry($"数据/{i:D3}.txt");
                entry.LastWriteTime = new DateTimeOffset(new DateTime(2020, 1, 1).AddDays(230 - i));
                using var writer = new StreamWriter(entry.Open()); writer.Write(i);
            }
        var window = app.CreateWindow();
        window.Width = SystemParameters.PrimaryScreenWidth + 80;
        window.Height = SystemParameters.PrimaryScreenHeight + 80;
        window.Show();
        var keyboard = new FixtureKeyboard();
        var grid = window.TestGrid;
        void Press(Key key, ModifierKeys modifiers = ModifierKeys.None, UIElement? target = null)
        {
            keyboard.Held = modifiers;
            var e = new KeyEventArgs(keyboard, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            (target ?? grid).RaiseEvent(e);
            Require(e.Handled, $"workspace handles {modifiers} {key}");
        }
        void Check(int focus, params int[] indices)
        {
            var selected = grid.SelectedItems.Cast<EntryView>().Select(r => int.Parse(r.Row.Id)).Order().ToArray();
            Require((grid.CurrentItem as EntryView)?.Row.Id == focus.ToString() && selected.SequenceEqual(indices),
                $"focus {focus} and selected IDs [{string.Join(",", indices)}] are independent");
        }
        void Click(Button b) { Require(b.IsVisible && b.IsEnabled, $"reachable button {b.Name}"); b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); }
        static bool Fits(FrameworkElement child, FrameworkElement parent)
        {
            var xy = child.TranslatePoint(new Point(), parent);
            return child.IsVisible && child.ActualHeight > 0 && child.ActualWidth > 0 && xy.X >= -1 && xy.Y >= -1 &&
                xy.X + child.ActualWidth <= parent.ActualWidth + 1 && xy.Y + child.ActualHeight <= parent.ActualHeight + 1;
        }
        try
        {
            await window.TestOpenAsync(path); await window.GoToAddressAsync("数据");
            await window.SortByAsync(4);
            grid.SelectedIndex = 0;
            Require(window.ModifiedColumn.SortMemberPath == "Modified" && (grid.SelectedItem as EntryView)?.Name == "229.txt" &&
                window.DetailModified.Text == new DateTime(2020, 1, 2).ToString("g"), "date sorting and details use the stored local timestamp");
            await window.SortByAsync(4);
            Require(grid.Items.Cast<EntryView>().First().Name == "000.txt", "descending time sort uses the full archive index");
            await window.SortByAsync(0);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Require(MonitorWorkArea.TryBounds(window, out var hwnd, out var work) && hwnd.X >= work.X - 1 && hwnd.Y >= work.Y - 1 &&
                hwnd.X + hwnd.Width <= work.X + work.Width + 1 && hwnd.Y + hwnd.Height <= work.Y + work.Height + 1,
                "oversized startup window fits the physical monitor work area");
            Require(grid.Items.Cast<EntryView>().First().SizeLabel == ClientApp.Text.Format("ClientScaledSize", 1, "B"),
                "one-byte entries show their real size");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                var sample = new AileArc.DesktopBridge.BrowserRow("0", "x", "", "x", "file", null, "—", false);
                Require(new EntryView(sample with { UnpackedBytes = 1536 }).SizeLabel == "1,5 KiB" &&
                    new EntryView(sample with { Size = 1024 }).SizeLabel == "1 MiB" &&
                    new EntryView(sample with { UnpackedBytes = 0 }).SizeLabel == "0 B" &&
                    new EntryView(sample).SizeLabel == "—", "localized units preserve exact, legacy, empty and unknown sizes");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
            Press(Key.Home); Press(Key.Down, ModifierKeys.Control); Check(1, 0);
            Press(Key.Space, ModifierKeys.Control); Check(1, 0, 1);
            Press(Key.Down, ModifierKeys.Control); Press(Key.Down, ModifierKeys.Control); Press(Key.Space); Check(3, 0, 1, 3);
            Press(Key.Down, ModifierKeys.Shift); Check(4, 3, 4);
            Press(Key.End, ModifierKeys.Control); Check(199, 3, 4);
            Press(Key.Space, ModifierKeys.Control);
            Press(Key.Up, ModifierKeys.Control | ModifierKeys.Shift); Check(198, 3, 4, 198, 199);
            Press(Key.Down, ModifierKeys.Control | ModifierKeys.Shift); Check(199, 3, 4, 199);
            Require(Equals(window.ExtractButton.Content, ClientApp.Text.Format("ClientExtractSelection", 3)), "extraction uses selected rows, not just focus");
            string output = Path.Combine(root, "键盘解压");
            await window.ExtractToAsync(output, false, true);
            var files = Directory.GetFiles(Path.Combine(output, "数据")).Select(Path.GetFileName).Order().ToArray();
            Require(files.SequenceEqual(["003.txt", "004.txt", "199.txt"]), "only keyboard-selected entries reach the filesystem");
            await window.RefreshArchiveAsync();
            // Refresh preserves selection, while keyboard focus deliberately starts a new interaction.
            Press(Key.End, ModifierKeys.Control);

            foreach (uint dpi in new uint[] { 96, 120, 144, 192 })
            {
                var area = MonitorWorkArea.LogicalSize(1366, 728, dpi);
                window.Width = Math.Min(1320, area.Width); window.Height = area.Height;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var current = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentItem);
                Require(current is not null && current.TranslatePoint(new Point(), grid).Y >= -1 &&
                    current.TranslatePoint(new Point(), grid).Y + current.ActualHeight <= grid.ActualHeight + 1,
                    $"focused row remains in the resized viewport at {dpi} DPI");
                Require(window.ActualWidth <= area.Width + 1 && window.ActualHeight <= area.Height + 1 &&
                    Fits(window.ExtractButton, window.BrowserBody) && Fits(window.SearchBox, window.BrowserBody) &&
                    Fits(window.EncodingBox, window.BrowserBody) && Fits(window.NextButton, window.BrowserBody) &&
                    window.AddressBar.ActualWidth >= 60 && grid.ActualHeight >= 80, $"usable viewport for a 1366×728 work area at {dpi} DPI");
                RenderViewport(window, dpi, outputDirectory);
                if (window.ShowDetails.IsVisible)
                {
                    Click(window.ShowDetails);
                    Require(Fits(window.HideDetails, window.DetailPane) && window.DetailColumn.ActualWidth == 0, "narrow inspector overlays the list");
                    Press(Key.Escape, target: window.HideDetails);
                }
                if (window.ShowFolders.IsVisible)
                {
                    Click(window.ShowFolders); Require(Fits(window.Folders, window.TreePane) && window.Folders.ActualHeight >= 40, "folder tree remains usable");
                    Click(window.ToggleTools);
                    Require(Fits(window.SideCommands, window.TreePane) && window.SideCommands.ScrollableHeight > 0, "all commands can be reached by scrolling");
                    window.SideCommands.ScrollToBottom(); window.UpdateLayout();
                    Require(Fits(window.LanguageBox, window.SideCommands), "language selector is reachable at the bottom");
                    Press(Key.Escape, target: window.HideFolders);
                }
                Check(199, 3, 4, 199);
            }
            double originalNameWidth = window.NameColumn.ActualWidth;
            window.NameColumn.Width = originalNameWidth + 500;
            grid.UpdateLayout();
            var horizontal = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
            Require(horizontal.ScrollableWidth > 0 && horizontal.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
                "resized columns remain horizontally reachable");
            var bar = (System.Windows.Controls.Primitives.ScrollBar)horizontal.Template.FindName("PART_HorizontalScrollBar", horizontal);
            Require(bar.ActualWidth >= grid.ActualWidth / 2 && !bar.Track.IsDirectionReversed &&
                bar.Track.IncreaseRepeatButton.Command == System.Windows.Controls.Primitives.ScrollBar.PageRightCommand,
                "horizontal scroll track spans the viewport and offers forward paging");
            System.Windows.Controls.Primitives.ScrollBar.PageRightCommand.Execute(null, bar.Track.IncreaseRepeatButton);
            grid.UpdateLayout();
            Require(horizontal.HorizontalOffset > 0, "paging the horizontal track reveals off-screen columns");
            window.NameColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            var dialog = new ActionDialog(window, "Extract", "Extract")
            {
                MaxHeight = 330, MaxWidth = 625
            };
            for (int i = 0; i < 18; i++) dialog.Explain($"test row {i}");
            dialog.Show();
            try
            {
                dialog.UpdateLayout();
                Require(dialog.ActualHeight <= 331 && Fits(dialog.Accept, dialog), "long dialog keeps its confirmation button within a short work area");
                var scroller = FindScroll(dialog.Body);
                Require(scroller is { ScrollableHeight: > 0 }, "long dialog body is scrollable");
            }
            finally { dialog.Close(); }
            Press(Key.Home, ModifierKeys.Control); Check(0, 3, 4, 199);
            string? opened = null; window.LaunchFile = (file, _) => opened = File.ReadAllText(file);
            Press(Key.Enter);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (window.IsBusy && DateTime.UtcNow < until) await Task.Delay(10);
            Require(opened == "0", "Enter opens the focused file rather than a previous selection");
        }
        finally { window.ApproveClose(); window.Close(); }

        static ScrollViewer? FindScroll(FrameworkElement item)
        {
            for (DependencyObject? p = item; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
                if (p is ScrollViewer viewer) return viewer;
            return null;
        }
    }

    private static void RenderViewport(MainWindow window, uint dpi, string? outputDirectory, string? scene = null)
    {
        var surface = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(surface.ActualWidth * dpi / 96), height = (int)Math.Ceiling(surface.ActualHeight * dpi / 96);
        var drawing = new DrawingVisual();
        var viewport = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, viewport);
            dc.DrawRectangle(new VisualBrush(surface) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewport }, null, viewport);
        }
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        // An unpainted/invisible visual can have valid layout coordinates.
        var pixels = new int[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        int opaque = 0, light = 0;
        foreach (int pixel in pixels)
        {
            if ((uint)pixel >> 24 != 255) continue;
            opaque++;
            if ((pixel & 255) > 145 && ((pixel >> 8) & 255) > 145 && ((pixel >> 16) & 255) > 145) light++;
        }
        if (outputDirectory is not null)
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            var name = scene is null ? $"{ClientApp.Text.Language}-{dpi}" : $"{ClientApp.Text.Language}-{scene}";
            using var output = new FileStream(Path.Combine(outputDirectory, name + ".png"), FileMode.CreateNew, FileAccess.Write);
            png.Save(output);
        }
        Require(opaque > pixels.Length * .98 && light > 200, $"painted viewport at {dpi} DPI ({opaque} opaque, {light} light)");
    }
}
