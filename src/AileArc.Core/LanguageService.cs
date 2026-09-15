using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace AileArc.Core;

/// <summary>Embedded .resw tables. Language is captured once for the process lifetime.</summary>
public sealed class LanguageService
{
    public const string DefaultLanguage = "zh-CN";
    public static readonly string[] Supported = ["zh-CN", "en-US"];
    private readonly Dictionary<string, string> fallback;
    private readonly Dictionary<string, string> selected;
    public string Language { get; }
    public LanguageService(string language)
    {
        Language = Supported.Contains(language) ? language : DefaultLanguage;
        fallback = Load(DefaultLanguage);
        selected = Language == DefaultLanguage ? fallback : Load(Language);
    }
    public string this[string key] => selected.GetValueOrDefault(key) ?? fallback.GetValueOrDefault(key) ?? key;
    public string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, this[key], values);
    public static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(LanguageService).Assembly.GetManifestResourceStream($"AileArc.Core.Strings.{language}.Resources.resw")
            ?? throw new InvalidOperationException("Language resources missing.");
        return XDocument.Load(stream).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
    }
}

public sealed record AppSettings(string Language = LanguageService.DefaultLanguage)
{
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AileArc", "settings.json");
    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(SettingsPath));
            return settings is not null && LanguageService.Supported.Contains(settings.Language) ? settings : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public async Task SaveAsync()
    {
        if (!LanguageService.Supported.Contains(Language)) throw new ArgumentException("Unsupported language.");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this), new System.Text.UTF8Encoding(false));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
