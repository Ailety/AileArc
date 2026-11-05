using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AileArc.Core.Compression;

namespace AileArc.Client;

public partial class MainWindow
{
    internal CompressionResult? LastCreated { get; private set; }
    private async void Create_Click(object sender, RoutedEventArgs e) => await ShowCreateDialogAsync([]);
    internal async Task ShowCreateDialogAsync(string[] initialSources)
    {
        if (IsBusy || !((ClientApp)Application.Current).CanStart(this)) return;
        var dialog = new ActionDialog(this, "CreateArchive", "Create");
        var sources = new ObservableCollection<string>();
        var selected = new ListBox { ItemsSource = sources, SelectionMode = SelectionMode.Extended, MaxHeight = 100, MinHeight = 40, Background = (Brush)dialog.FindResource("CanvasBrush"), Foreground = dialog.Foreground, BorderBrush = (Brush)dialog.FindResource("LineBrush") };
        var destination = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(destination, Text["ArchiveDestination"]);
        var buttons = new WrapPanel();
        var files = new Button { Content = Text["AddFiles"] };
        var folder = new Button { Content = Text["AddFolder"] };
        var clear = new Button { Content = Text["ClearSelection"] };
        var remove = new Button { Content = Text["ClientRemoveSources"], IsEnabled = false };
        buttons.Children.Add(files); buttons.Children.Add(folder); buttons.Children.Add(remove); buttons.Children.Add(clear);
        dialog.Body.Children.Add(buttons); dialog.Body.Children.Add(selected);
        dialog.Explain(Text["ArchiveDestination"]); dialog.Body.Children.Add(destination);
        var hint = new TextBlock { Text = Text["ClientLocalFileHint"], TextWrapping = TextWrapping.Wrap };
        dialog.Body.Children.Add(hint);
        var browse = new Button { Content = Text["ClientChooseOutput"] }; dialog.Body.Children.Add(browse);
        var format = dialog.Choices(Text["ArchiveFormat"], ["ZIP", "7Z"]);
        var preset = dialog.Choices(Text["CompressionPreset"], [Text["PresetFast"], Text["PresetBalanced"], Text["PresetMaximum"]]); preset.SelectedIndex = 1;
        dialog.Explain(Text["OptionalPassword"]);
        var secret = new PasswordBox { MaxLength = 4096, Padding = new Thickness(10), Background = (Brush)dialog.FindResource("CanvasBrush"), Foreground = dialog.Foreground };
        System.Windows.Automation.AutomationProperties.SetName(secret, Text["OptionalPassword"]);
        dialog.Body.Children.Add(secret);
        var names = new CheckBox { Content = Text["EncryptNames"], IsEnabled = false, Margin = new Thickness(0, 12, 0, 12) }; dialog.Body.Children.Add(names);
        dialog.Explain(Text["CreateArchiveHint"]);
        void Refresh()
        {
            clear.IsEnabled = sources.Count > 0;
            bool validPath = ActionDialog.IsLocalDestination(destination.Text) && Path.GetFileName(destination.Text).Length != 0;
            hint.Text = Text["ClientLocalFileHint"];
            hint.Visibility = !validPath && destination.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.Accept.IsEnabled = sources.Count > 0 && validPath;
            names.IsEnabled = format.SelectedIndex == 1 && secret.Password.Length > 0;
            if (!names.IsEnabled) names.IsChecked = false;
        }
        void RemoveSelected()
        {
            int first = selected.SelectedIndex;
            foreach (var path in selected.SelectedItems.Cast<string>().ToArray()) sources.Remove(path);
            if (first >= 0 && sources.Count > 0) selected.SelectedIndex = Math.Min(first, sources.Count - 1);
            Refresh();
        }
        selected.SelectionChanged += (_, _) => remove.IsEnabled = selected.SelectedItems.Count > 0;
        selected.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && selected.SelectedItems.Count > 0) { e.Handled = true; RemoveSelected(); }
        };
        remove.Click += (_, _) => RemoveSelected();
        void Add(IEnumerable<string> paths)
        {
            foreach (string path in paths) if (!sources.Contains(path, StringComparer.OrdinalIgnoreCase)) sources.Add(path);
            if (sources.Count > 0 && string.IsNullOrWhiteSpace(destination.Text) && Path.GetDirectoryName(sources[0]) is { } directory)
                destination.Text = Path.Combine(directory, Path.GetFileNameWithoutExtension(sources[0]) + (format.SelectedIndex == 0 ? ".zip" : ".7z"));
            Refresh();
        }
        files.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = Text["ClientAllFilesFilter"] }; if (picker.ShowDialog(dialog) == true) Add(picker.FileNames); };
        folder.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(dialog) == true) Add([picker.FolderName]); };
        clear.Click += (_, _) => { sources.Clear(); Refresh(); };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(destination.Text), Filter = format.SelectedIndex == 0 ? "ZIP|*.zip" : "7Z|*.7z", DefaultExt = format.SelectedIndex == 0 ? ".zip" : ".7z", OverwritePrompt = false };
            if (picker.ShowDialog(dialog) == true) destination.Text = picker.FileName;
        };
        destination.TextChanged += (_, _) => Refresh();
        secret.PasswordChanged += (_, _) => Refresh();
        format.SelectionChanged += (_, _) => { if (destination.Text.Length > 0) destination.Text = Path.ChangeExtension(destination.Text, format.SelectedIndex == 0 ? ".zip" : ".7z"); Refresh(); };
        Refresh();
        Add(initialSources);
        CompressionDestination? captured = null;
        dialog.BeforeAccept = async () =>
        {
            string destinationPath = destination.Text.Trim();
            try { captured = await Task.Run(() => CompressionDestination.Capture(destinationPath)); }
            catch (AileArc.Core.ArchiveOperationException e)
            {
                hint.Text = Text[e.Code]; hint.Visibility = Visibility.Visible; return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            { hint.Text = Text["CompressionTargetUnavailable"]; hint.Visibility = Visibility.Visible; return false; }
            return dialog.IsVisible && (captured.Existing is null || Confirm("ReplaceArchive",
                captured.FullPath + Environment.NewLine + Text["ReplaceArchiveHint"], "Replace", owner: dialog));
        };
        try
        {
            if (dialog.ShowDialog() != true || captured is null) return;
            var options = new CompressionOptions(sources.ToArray(), captured.FullPath, format.SelectedIndex == 0 ? "ZIP" : "7Z",
                preset.SelectedIndex switch { 0 => "Fast", 2 => "Maximum", _ => "Balanced" },
                secret.Password.Length == 0 ? null : secret.Password, names.IsChecked == true,
                Overwrite: captured.Existing is not null, ConfirmedDestination: captured);
            secret.Clear();
            await CreateArchiveAsync(options);
        }
        finally { secret.Clear(); }
    }
    internal Task CreateArchiveAsync(CompressionOptions options)
    {
        return RunAsync(Text["Compressing"], async token =>
        {
            LastCreated = null;
            int version = generation;
            using var report = new WindowProgress<CompressionProgress>(Dispatcher, p =>
            {
                if (closed || !IsBusy || version != generation || token.IsCancellationRequested) return;
                StatusText.Text = p.Phase == "verifying" ? Text["VerifyingArchive"] : Text.Format("CompressionProgress", p.Completed / 1048576.0, p.Total / 1048576.0);
            });
            var result = await Task.Run(() => worker.CreateAsync(options, report, token), token);
            return () => { LastCreated = result; StatusText.Text = Text.Format("ArchiveCreated", result.Entries, result.Destination); };
        }, false);
    }

    private void ShowCreatedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBusy && LastCreated is { } result) LaunchFolder(Path.GetDirectoryName(result.Destination)!);
    }
}
