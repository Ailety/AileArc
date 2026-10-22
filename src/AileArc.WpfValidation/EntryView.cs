using System.ComponentModel;
using System.Globalization;
using AileArc.DesktopBridge;

namespace AileArc.WpfValidation;

public sealed class EntryView(BrowserRow row) : INotifyPropertyChanged
{
    private bool selected;
    public BrowserRow Row { get; } = row;
    public string Name => Row.Name;
    public override string ToString() => Name;
    public string Path => Row.Path;
    public string TypeLabel => Row.Type switch { "folder" => "文件夹", "md" => "Markdown", "code" => "代码 / 配置", "text" => "文本文档", _ => "文件" };
    public string SizeLabel => Row.Type == "folder" ? "—" : Row.Size?.ToString("N2", CultureInfo.CurrentCulture) ?? "—";
    public string Glyph => Row.Type == "folder" ? "\uE8B7" : "\uE8A5";
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
