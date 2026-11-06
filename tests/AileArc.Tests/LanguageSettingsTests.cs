using AileArc.Core;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Fact]
    public async Task MissingDamagedOrOversizedLanguagePreferencesFallBackToChinese()
    {
        string path = Path.Combine(temp, "settings.json");
        Assert.Equal("zh-CN", (await AppSettings.LoadAsync(path)).Language);
        foreach (string content in new[] { "{", """{"Language":"unknown"}""", new string(' ', 70_000) + """{"Language":"en-US"}""" })
        {
            await File.WriteAllTextAsync(path, content);
            Assert.Equal("zh-CN", (await AppSettings.LoadAsync(path)).Language);
        }
        await new AppSettings("en-US").SaveAsync(path);
        Assert.Equal("en-US", (await AppSettings.LoadAsync(path)).Language);
    }

    [Fact]
    public async Task FailedLanguageReplacementPreservesPreferenceAndRemovesOnlyItsTemporary()
    {
        string path = Path.Combine(temp, "settings.json");
        await new AppSettings("en-US").SaveAsync(path);
        byte[] original = await File.ReadAllBytesAsync(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => new AppSettings("zh-CN").SaveAsync(path));
            Assert.True(failure is UnauthorizedAccessException or IOException);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => new AppSettings("invalid").SaveAsync(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(temp)));
    }
}
