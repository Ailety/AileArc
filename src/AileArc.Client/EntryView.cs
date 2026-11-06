using System.ComponentModel;
using System.Globalization;
using AileArc.DesktopBridge;

namespace AileArc.Client;

public sealed class EntryView(BrowserRow row) : INotifyPropertyChanged
{
    private bool selected;
    public BrowserRow Row { get; } = row;
    public string Name => Row.Name;
    public override string ToString() => Name;
    public string Path => Row.Path;
    public string TypeLabel => LabelFor(Row.Type);
    internal static string LabelFor(string kind) => ClientApp.Text[Appearance(kind).LabelKey];
    public string Glyph => Appearance(Row.Type).Glyph;
    public string IconTone => Appearance(Row.Type).Tone;

    // Names and glyphs are display hints. Only the Worker identifies archive contents.
    private static (string LabelKey, string Glyph, string Tone) Appearance(string kind) => kind switch
    {
        "folder"       => ("ClientFolder",           "\uE8B7", "archive"),
        "archive"      => ("ClientKindArchive",      "\uF012", "archive"),
        "pdf"          => ("ClientKindPdf",          "\uEA90", "paper"),
        "document"     => ("ClientKindDocument",     "\uE9F9", "paper"),
        "spreadsheet"  => ("ClientKindSpreadsheet",  "\uF0E2", "paper"),
        "presentation" => ("ClientKindPresentation", "\uE786", "paper"),
        "md"           => ("ClientKindMarkdown",     "\uE736", "paper"),
        "text"         => ("ClientText",             "\uE8E4", "paper"),
        "image"        => ("ClientKindImage",        "\uEB9F", "media"),
        "audio"        => ("ClientKindAudio",        "\uEC4F", "media"),
        "video"        => ("ClientKindVideo",        "\uE8B2", "media"),
        "code"         => ("ClientCode",             "\uE943", "neutral"),
        "application"  => ("ClientKindApplication",  "\uED35", "neutral"),
        _              => ("ClientFile",             "\uE7C3", "neutral")
    };
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    public string SizeLabel
    {
        get
        {
            if (Row.Type == "folder") return "—";
            double bytes = Row.UnpackedBytes is ulong exact ? exact : (Row.Size ?? double.NaN) * 1024;
            return FormatSize(bytes);
        }
    }
    internal static string FormatSize(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "—";
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1) { bytes /= 1024; unit++; }
        return ClientApp.Text.Format("ClientScaledSize", bytes.ToString(unit == 0 ? "N0" : "#,0.##", CultureInfo.CurrentCulture), Units[unit]);
    }
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
