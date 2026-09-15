namespace AileArc.Core.WorkCopies;

public static class ExternalOpenPolicy
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msp", ".scr", ".cpl", ".reg", ".lnk", ".url", ".scf", ".settingcontent-ms", ".appref-ms", ".application",
        ".jar", ".chm", ".xll", ".dll"
    };
    public static bool RequiresConfirmation(string path) => ExecutableExtensions.Contains(Path.GetExtension(path));
}
