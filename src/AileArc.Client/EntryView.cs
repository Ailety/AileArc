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
    internal static string LabelFor(string kind) => kind switch { "folder" => ClientApp.Text["ClientFolder"], "md" => "Markdown", "code" => ClientApp.Text["ClientCode"], "text" => ClientApp.Text["ClientText"], _ => ClientApp.Text["ClientFile"] };
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    public string SizeLabel
    {
        get
        {
            if (Row.Type == "folder") return "—";
            double bytes = Row.UnpackedBytes is ulong exact ? exact : (Row.Size ?? double.NaN) * 1024;
            if (!double.IsFinite(bytes) || bytes < 0) return "—";
            int unit = 0;
            while (bytes >= 1024 && unit < Units.Length - 1) { bytes /= 1024; unit++; }
            return ClientApp.Text.Format("ClientScaledSize", bytes.ToString(unit == 0 ? "N0" : "#,0.##", CultureInfo.CurrentCulture), Units[unit]);
        }
    }
    public string Glyph => Row.Type == "folder" ? "\uE8B7" : "\uE8A5";
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
