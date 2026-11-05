using System.ComponentModel;
using AileArc.Core.Extraction;
using AileArc.Core.Storage;
using AileArc.Shared;

namespace AileArc.Core.Compression;

public sealed record CompressionDestination(string FullPath, ArchiveIdentity? Existing)
{
    public static CompressionDestination Capture(string path)
    {
        string full = Path.GetFullPath(path);
        try
        {
            using var parent = DirectoryGuard.Acquire(Path.GetDirectoryName(full) ?? throw new ArchiveOperationException("CompressionOptionsInvalid"), create: false);
            using var file = VerifiedFile.OpenRegularInput(full, FileShare.Read);
            return new(full, ArchiveFileIdentity.Read(file));
        }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) { return new(full, null); }
        catch (Win32Exception) { throw new ArchiveOperationException("CompressionTargetUnavailable", full); }
    }

    internal void Check(string destination)
    {
        if (!string.Equals(FullPath, destination, StringComparison.OrdinalIgnoreCase) || Existing != Capture(destination).Existing)
            throw new ArchiveOperationException("CompressionTargetChanged", destination);
    }
}
