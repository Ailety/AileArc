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
    public static Task<AppSettings> LoadAsync() => LoadAsync(SettingsPath);
    public static async Task<AppSettings> LoadAsync(string settingsPath)
    {
        try
        {
            using var input = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (input.Length > 64 * 1024) return new();
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(input).ConfigureAwait(false);
            return settings is not null && LanguageService.Supported.Contains(settings.Language) ? settings : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException) { return new(); }
    }
    public Task SaveAsync() => SaveAsync(SettingsPath);
    public async Task SaveAsync(string settingsPath)
    {
        if (!LanguageService.Supported.Contains(Language)) throw new ArgumentException("Unsupported language.");
        settingsPath = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        string temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool ownsTemp = false;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                ownsTemp = true;
                await JsonSerializer.SerializeAsync(output, this).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, settingsPath, overwrite: true);
            ownsTemp = false;
        }
        finally { if (ownsTemp) File.Delete(temp); }
    }
}
