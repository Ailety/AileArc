using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AileArc.Client;

internal static partial class ClientChecks
{
    private static async Task CheckFileKindsAsync(MainWindow window, string root, string? outputDirectory)
    {
        string archive = Path.Combine(root, "Studio handoff.zip");
        var examples = new Dictionary<string, string>
        {
            ["Artwork"] = "ClientFolder", ["Layout.PDF"] = "ClientKindPdf", ["Photo.jpg"] = "ClientKindImage",
            ["Assets.7z"] = "ClientKindArchive", ["Brief.docx"] = "ClientKindDocument", ["Budget.xlsx"] = "ClientKindSpreadsheet",
            ["Slides.pptx"] = "ClientKindPresentation", ["Narration.mp3"] = "ClientKindAudio", ["Trailer.mp4"] = "ClientKindVideo",
            ["Viewer.exe"] = "ClientKindApplication", ["config.yml"] = "ClientCode", ["README.md"] = "ClientKindMarkdown",
            ["Notes.txt"] = "ClientText", ["Draft.pdf.unclassified"] = "ClientFile"
        };
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (string name in examples.Keys)
            {
                if (name == "Artwork") { zip.CreateEntry("Artwork/cover.svg"); continue; }
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }

        await window.TestOpenAsync(archive);
        var grid = window.TestGrid;
        var rows = grid.Items.Cast<EntryView>().ToDictionary(item => item.Name);
        Require(rows.Count == examples.Count, "mixed archive exposes the actual root entries");
        foreach (var (name, key) in examples)
        {
            var item = rows[name];
            grid.SelectedItem = item;
            grid.ScrollIntoView(item, window.TypeColumn);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var label = ClientApp.Text[key];
            Require(item.TypeLabel == label && window.DetailType.Text == label &&
                window.TypeColumn.GetCellContent(item) is TextBlock { } type && type.Text == label,
                $"type column and inspector agree on {name} in this language");
            grid.ScrollIntoView(item, window.NameColumn);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Require(window.NameColumn.GetCellContent(item) is { } nameCell && HasGlyph(nameCell, item.Glyph),
                $"realized name cell contains the glyph for {name}");
        }
        Require(rows.Values.Select(row => row.Glyph).Distinct().Count() == examples.Count,
            "all fourteen file classifications have dedicated glyphs");

        foreach (bool descending in new[] { false, true })
        {
            await window.SortByAsync(2);
            var ordered = grid.Items.Cast<EntryView>().ToArray();
            var labels = ordered.Skip(1).Select(item => item.TypeLabel).ToArray();
            Require(ordered[0].Name == "Artwork" && labels.SequenceEqual(descending
                ? labels.OrderDescending(StringComparer.OrdinalIgnoreCase) : labels.Order(StringComparer.OrdinalIgnoreCase)),
                "type sort keeps directories first and orders files by their displayed label");
        }
        await window.SortByAsync(0);
        var entries = grid.Items.Cast<EntryView>().ToDictionary(item => item.Name);
        grid.SelectedItem = entries["Layout.PDF"];
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (outputDirectory is not null) RenderViewport(window, 96, outputDirectory, "file-kinds");

        string? launched = null; int prompts = 0;
        var previousLauncher = window.LaunchFile;
        var previousConfirm = window.ConfirmOverride;
        try
        {
            window.LaunchFile = (file, _) => launched = file;
            window.ConfirmOverride = (_, _) => { prompts++; return false; };
            await window.OpenExternalAsync(long.Parse(entries["Viewer.exe"].Row.Id, CultureInfo.InvariantCulture));
            Require(prompts == 1 && launched is null, "application icon preserves executable confirmation");
            await window.OpenExternalAsync(long.Parse(entries["config.yml"].Row.Id, CultureInfo.InvariantCulture));
            Require(prompts == 1 && launched is not null && File.ReadAllText(launched) == "config.yml",
                "default application receives the unchanged configuration working copy");
        }
        finally { window.LaunchFile = previousLauncher; window.ConfirmOverride = previousConfirm; }
        grid.UnselectAll(); grid.SelectedItems.Add(entries["Layout.PDF"]); grid.SelectedItems.Add(entries["Draft.pdf.unclassified"]);
        string extracted = Path.Combine(root, "mixed-selected");
        await window.ExtractToAsync(extracted, false, true);
        Require(Directory.GetFiles(extracted).Length == 2 &&
            File.ReadAllText(Path.Combine(extracted, "Layout.PDF")) == "Layout.PDF" &&
            File.ReadAllText(Path.Combine(extracted, "Draft.pdf.unclassified")) == "Draft.pdf.unclassified",
            "selection and extraction use entry identity regardless of file kind");
    }

    private static bool HasGlyph(DependencyObject node, string glyph)
    {
        if (node is TextBlock { IsVisible: true } text && text.Text == glyph && glyph.Length > 0) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasGlyph(VisualTreeHelper.GetChild(node, i), glyph)) return true;
        return false;
    }
}
