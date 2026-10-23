using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        var sources = new List<string>();
        var selected = new ListBox { MaxHeight = 100, MinHeight = 40, Background = (Brush)dialog.FindResource("CanvasBrush"), Foreground = dialog.Foreground, BorderBrush = (Brush)dialog.FindResource("LineBrush") };
        var destination = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(destination, Text["ArchiveDestination"]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var files = new Button { Content = Text["AddFiles"] };
        var folder = new Button { Content = Text["AddFolder"] };
        var clear = new Button { Content = Text["ClearSelection"] };
        buttons.Children.Add(files); buttons.Children.Add(folder); buttons.Children.Add(clear);
        dialog.Body.Children.Add(buttons); dialog.Body.Children.Add(selected);
        dialog.Explain(Text["ArchiveDestination"]); dialog.Body.Children.Add(destination);
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
            selected.ItemsSource = sources.ToArray(); dialog.Accept.IsEnabled = sources.Count > 0 && !string.IsNullOrWhiteSpace(destination.Text);
            names.IsEnabled = format.SelectedIndex == 1 && secret.Password.Length > 0;
            if (!names.IsEnabled) names.IsChecked = false;
        }
        void Add(IEnumerable<string> paths)
        {
            foreach (string path in paths) if (!sources.Contains(path, StringComparer.OrdinalIgnoreCase)) sources.Add(path);
            if (sources.Count > 0 && string.IsNullOrWhiteSpace(destination.Text))
                destination.Text = Path.Combine(Path.GetDirectoryName(sources[0])!, Path.GetFileNameWithoutExtension(sources[0]) + (format.SelectedIndex == 0 ? ".zip" : ".7z"));
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
        try
        {
            if (dialog.ShowDialog() != true) return;
            string target = destination.Text.Trim();
            bool overwrite = File.Exists(target);
            if (overwrite && !Confirm("ReplaceArchive", target + Environment.NewLine + Text["ReplaceArchiveHint"], "Replace")) return;
            var options = new CompressionOptions(sources.ToArray(), target, format.SelectedIndex == 0 ? "ZIP" : "7Z",
                preset.SelectedIndex switch { 0 => "Fast", 2 => "Maximum", _ => "Balanced" },
                secret.Password.Length == 0 ? null : secret.Password, names.IsChecked == true, overwrite);
            secret.Clear();
            await CreateArchiveAsync(options);
        }
        finally { secret.Clear(); }
    }
    internal Task CreateArchiveAsync(CompressionOptions options)
    {
        LastCreated = null;
        return RunAsync(Text["Compressing"], async token =>
        {
            int version = generation;
            var report = new Progress<CompressionProgress>(p =>
            {
                if (closed || !IsBusy || version != generation || token.IsCancellationRequested) return;
                StatusText.Text = p.Phase == "verifying" ? Text["VerifyingArchive"] : Text.Format("CompressionProgress", p.Completed / 1048576.0, p.Total / 1048576.0);
            });
            var result = await Task.Run(() => worker.CreateAsync(options, report, token), token);
            return () => { LastCreated = result; StatusText.Text = Text.Format("ArchiveCreated", result.Entries, result.Destination); };
        }, false);
    }
}
