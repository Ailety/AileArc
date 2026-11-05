using AileArc.Shared;
namespace AileArc.Core.Lifecycle;
public static class ArchiveWindowIdentity
{
    public static async Task<string?> ReadAsync(string path) => (await ReadSnapshotAsync(path))?.FileId;
    public static async Task<ArchiveIdentity?> ReadSnapshotAsync(string path)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return ArchiveFileIdentity.Read(file);
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { return null; }
    }
}
