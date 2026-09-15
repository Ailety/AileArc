using System.Text.RegularExpressions;

namespace AileArc.Core.Extraction;

public static partial class ArchivePathPolicy
{
    /// <summary>Reject ambiguous Windows names rather than silently altering archive paths.</summary>
    public static string Validate(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || archivePath.Length > 32000)
            throw new ArchiveOperationException("UnsafePath");
        string normalized = archivePath.Replace('\\', '/').TrimEnd('/');
        if (normalized.StartsWith('/') || normalized.Length == 0) throw new ArchiveOperationException("UnsafePath");
        string[] segments = normalized.Split('/');
        if (segments.Length > 128) throw new ArchiveOperationException("ResourceLimit");
        foreach (var segment in segments)
        {
            if (segment is "" or "." or ".." || segment.Length > 255 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*') || DeviceName().IsMatch(segment))
                throw new ArchiveOperationException("UnsafePath");
        }
        return Path.Combine(segments);
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
}
