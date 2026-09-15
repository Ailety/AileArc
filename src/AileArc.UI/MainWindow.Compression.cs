using AileArc.Core;
using AileArc.Core.Compression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace AileArc.UI;

public sealed partial class MainWindow
{
    private async Task CreateArchiveAsync()
    {
        if (operation is not null || dialogOpen) return;
        var sources = new List<string>();
        var selected = new ListView { MaxHeight = 120, MinHeight = 40 };
        var destination = new TextBox { Header = text["ArchiveDestination"], MinWidth = 460 };
        var format = new ComboBox { Header = text["ArchiveFormat"], ItemsSource = new[] { "ZIP", "7Z" }, SelectedIndex = 0, MinWidth = 110 };
        var preset = new ComboBox { Header = text["CompressionPreset"], ItemsSource = new[] { text["PresetFast"], text["PresetBalanced"], text["PresetMaximum"] }, SelectedIndex = 1, MinWidth = 160 };
        var password = new PasswordBox { Header = text["OptionalPassword"] };
        var encryptNames = new CheckBox { Content = text["EncryptNames"], IsEnabled = false };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["CreateArchive"],
            PrimaryButtonText = text["Create"], CloseButtonText = text["Cancel"], IsPrimaryButtonEnabled = false };
        void RefreshSources()
        {
            selected.ItemsSource = sources.ToArray();
            dialog.IsPrimaryButtonEnabled = sources.Count > 0;
            if (sources.Count > 0 && destination.Text.Length == 0)
            {
                string first = System.IO.Path.TrimEndingDirectorySeparator(sources[0]);
                destination.Text = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(first)!,
                    System.IO.Path.GetFileNameWithoutExtension(first) + (format.SelectedIndex == 0 ? ".zip" : ".7z"));
            }
        }
        async Task AddSources(bool folder)
        {
            try
            {
                if (folder)
                {
                    var result = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
                    if (result is not null && !sources.Contains(result.Path, StringComparer.OrdinalIgnoreCase)) sources.Add(result.Path);
                }
                else
                {
                    var result = await new FileOpenPicker(AppWindow.Id).PickMultipleFilesAsync();
                    foreach (var file in result) if (!sources.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) sources.Add(file.Path);
                }
                RefreshSources();
            }
            catch (Exception) { notice.Text = text["OpenFailed"]; }
        }
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Children.Add(Button(text["AddFiles"], () => AddSources(false)));
        add.Children.Add(Button(text["AddFolder"], () => AddSources(true)));
        add.Children.Add(Button(text["ClearSelection"], () => { sources.Clear(); RefreshSources(); return Task.CompletedTask; }));
        var parameters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        parameters.Children.Add(format); parameters.Children.Add(preset);
        format.SelectionChanged += (_, _) =>
        {
            encryptNames.IsEnabled = format.SelectedIndex == 1 && password.Password.Length > 0;
            if (!encryptNames.IsEnabled) encryptNames.IsChecked = false;
            if (destination.Text.Length > 0) destination.Text = System.IO.Path.ChangeExtension(destination.Text, format.SelectedIndex == 0 ? ".zip" : ".7z");
        };
        password.PasswordChanged += (_, _) =>
        {
            encryptNames.IsEnabled = format.SelectedIndex == 1 && password.Password.Length > 0;
            if (!encryptNames.IsEnabled) encryptNames.IsChecked = false;
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(add); panel.Children.Add(selected); panel.Children.Add(destination);
        panel.Children.Add(Button(text["BrowseFolder"], async () =>
        {
            try
            {
                var folder = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
                if (folder is not null) destination.Text = System.IO.Path.Combine(folder.Path,
                    destination.Text.Length == 0 ? "Archive" + (format.SelectedIndex == 0 ? ".zip" : ".7z") : System.IO.Path.GetFileName(destination.Text));
            }
            catch (Exception) { notice.Text = text["FolderPickerFailed"]; }
        }));
        panel.Children.Add(parameters); panel.Children.Add(password); panel.Children.Add(encryptNames);
        panel.Children.Add(new TextBlock { Text = text["CreateArchiveHint"], TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        dialog.Content = panel;
        dialogOpen = true;
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
        if (choice != ContentDialogResult.Primary) { password.Password = ""; return; }
        string outputPath = destination.Text.Trim();
        bool overwrite = false;
        if (await Task.Run(() => File.Exists(outputPath)))
        {
            var confirm = new ContentDialog { XamlRoot = root.XamlRoot, Title = text["ReplaceArchive"], Content = outputPath + "\n" + text["ReplaceArchiveHint"],
                PrimaryButtonText = text["Replace"], CloseButtonText = text["Cancel"] };
            dialogOpen = true;
            try { overwrite = await confirm.ShowAsync() == ContentDialogResult.Primary; }
            finally { dialogOpen = false; }
            if (!overwrite) { password.Password = ""; return; }
        }
        if (closed) { password.Password = ""; return; }
        var options = new CompressionOptions(sources.ToArray(), outputPath, format.SelectedIndex == 0 ? "ZIP" : "7Z",
            preset.SelectedIndex switch { 0 => "Fast", 2 => "Maximum", _ => "Balanced" },
            string.IsNullOrEmpty(password.Password) ? null : password.Password, encryptNames.IsChecked == true, overwrite);
        password.Password = "";
        var current = new CancellationTokenSource();
        operation = current;
        SetBusy(true); SetFailures([]);
        status.Text = text["Compressing"];
        try
        {
            var report = new Progress<CompressionProgress>(p =>
            {
                if (closed || !ReferenceEquals(operation, current) || current.IsCancellationRequested) return;
                status.Text = p.Phase == "verifying" ? text["VerifyingArchive"] : text.Format("CompressionProgress", p.Completed / (1024.0 * 1024), p.Total / (1024.0 * 1024));
            });
            var result = await Task.Run(() => client.CreateAsync(options, report, current.Token), current.Token);
            if (!closed) status.Text = text.Format("ArchiveCreated", result.Entries, result.Destination);
        }
        catch (OperationCanceledException) { if (!closed) status.Text = text["Cancelled"]; }
        catch (ArchiveOperationException error) { if (!closed) ShowFailure(error.Code); }
        catch (Exception) { if (!closed) ShowFailure("CompressionFailed"); }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!closed) SetBusy(false);
            current.Dispose();
        }
    }
}
