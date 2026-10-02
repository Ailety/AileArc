using AileArc.Core.Storage;
namespace AileArc.Core.Lifecycle;
public static class ArchiveWindowIdentity
{
    public static async Task<string?> ReadAsync(string path)
    {
        try { return await Task.Run(() => VerifiedFile.TryIdentity(path)).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { return null; }
    }
}
