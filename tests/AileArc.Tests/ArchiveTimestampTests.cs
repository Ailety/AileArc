using System.Globalization;
using AileArc.Core;
using AileArc.Core.Extraction;
using AileArc.DesktopBridge;
using AileArc.Shared;
using Xunit;

namespace AileArc.Tests;

public sealed partial class WorkerTests
{
    [Theory]
    [InlineData("ZIP")]
    [InlineData("7Z")]
    public async Task VerifiedExtractionRestoresReportedFileTimeAndLeavesSkippedFilesAlone(string format)
    {
        string source = Path.Combine(temp, "历史.txt"), archive = Path.Combine(temp, "sample." + format.ToLowerInvariant());
        var date = new DateTime(2012, 11, 16, 9, 20, 42, DateTimeKind.Utc);
        await File.WriteAllTextAsync(source, "past contents"); File.SetLastWriteTimeUtc(source, date);
        await client.CreateAsync(new([source], archive, format));
        var scan = await client.ScanAsync(archive);
        long stored = Assert.Single(scan.Entries).ModifiedFileTime!.Value;
        Assert.Equal(date, DateTime.FromFileTimeUtc(stored));
        string destination = Path.Combine(temp, "out"), file = Path.Combine(destination, Path.GetFileName(source));
        var result = await client.ExtractAsync(archive, new(destination, Smart: false));
        Assert.Empty(result.MetadataWarnings); Assert.Empty(result.Failures);
        Assert.Equal(stored, File.GetLastWriteTimeUtc(file).ToFileTimeUtc());
        var later = date.AddYears(2); File.SetLastWriteTimeUtc(file, later);
        var skipped = await client.ExtractAsync(archive, new(destination, Smart: false, ConflictPolicy: ConflictChoice.Skip));
        Assert.Equal(1, skipped.Skipped); Assert.Equal(later, File.GetLastWriteTimeUtc(file));
        await client.ExtractAsync(archive, new(destination, Smart: false, ConflictPolicy: ConflictChoice.Replace));
        Assert.Equal(date, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void FileDateOutsideRegionalCalendarStillDisplaysTheEntry()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var date = new DateTime(1850, 2, 1, 12, 0, 0, DateTimeKind.Utc);
            var archive = new ArchivePresentation(new("7Z", [new(0, "old", false, 1, false, ModifiedFileTime: date.ToFileTimeUtc())]));
            var row = Assert.Single(archive.Query(new(1, Guid.NewGuid().ToString(), "query")).Rows);
            Assert.Equal(date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), row.Date);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void TimeSortUsesRawValuesAndRetainsLateExplicitDirectoryMetadata()
    {
        long early = new DateTime(2012, 4, 3, 1, 1, 1, DateTimeKind.Utc).ToFileTimeUtc(), late = early + 10;
        ArchiveEntry[] entries = [new(0, "explicit/b", false, 1, false), new(1, "synthetic/c", false, 1, false),
            new(2, "explicit/", true, null, false, ModifiedFileTime: late), new(3, "a", false, 1, false, ModifiedFileTime: late),
            new(4, "z", false, 1, false, ModifiedFileTime: early), new(5, "unknown", false, 0, false)];
        var index = new ArchiveIndex(entries);
        Assert.Equal(new[] { "explicit", "synthetic", "z", "a", "unknown" }, index.Query("", sort: EntrySort.Modified).Select(x => x.Name));
        Assert.Equal(new[] { "explicit", "synthetic", "a", "z", "unknown" }, index.Query("", sort: EntrySort.Modified, descending: true).Select(x => x.Name));
        var presentation = new ArchivePresentation(new("ZIP", entries));
        var page = presentation.Query(new(1, Guid.NewGuid().ToString(), "query", Sort: "modified"));
        Assert.Equal(DateTime.FromFileTimeUtc(late).ToLocalTime().ToString("g"), page.Rows[0].Date);
        Assert.Equal("—", page.Rows[1].Date);
        Assert.Equal("4", page.Rows[2].Id);
    }
}
