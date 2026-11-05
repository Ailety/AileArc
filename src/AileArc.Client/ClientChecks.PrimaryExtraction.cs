using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckPrimaryExtractionAsync(MainWindow window, ClientApp app, string root)
    {
        string zipPath = Path.Combine(root, "选择范围.zip");
        string[] names = ["甲.txt", "乙.txt", "不要解压.txt"];
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        await window.TestOpenAsync(zipPath);
        Require(Equals(window.ExtractButton.Content, ClientApp.Text["ClientExtractAll"]), "empty selection offers whole archive");
        foreach (var row in window.TestGrid.Items.Cast<EntryView>().Where(r => names[..2].Contains(r.Name)).ToArray())
            window.TestGrid.SelectedItems.Add(row);
        string label = ClientApp.Text.Format("ClientExtractSelection", 2);
        Require(Equals(window.ExtractButton.Content, label), "primary action shows selected count");
        string target = Path.Combine(root, "仅解压两个");
        await ObserveDialogAsync(app, ClientApp.Text["Extract"], () =>
        {
            window.ExtractButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        }, dialog =>
        {
            Require(!dialog.Body.Children.OfType<ComboBox>().Any(), "no second scope selection");
            Require(dialog.Body.Children.OfType<TextBlock>().Any(t => t.Text == label), "destination dialog identifies selection");
            dialog.Body.Children.OfType<TextBox>().Single().Text = target;
        }, accept: true);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (window.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(!window.IsBusy && window.LastExtraction is { Completed: 2, Cancelled: false } result && result.Failures.Count == 0, "selected extraction completed");
        Require(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 2 && !File.Exists(Path.Combine(target, names[2])), "unselected file excluded");
        foreach (var name in names[..2]) Require(File.ReadAllText(Path.Combine(target, name)) == name, "selected file contents match");
        window.TestGrid.UnselectAll();
        Require(Equals(window.ExtractButton.Content, ClientApp.Text["ClientExtractAll"]), "clearing selection restores all action");
    }
}
