# Shared by the development installer and uninstaller. Windows PowerShell 5.1 compatible.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$product = 'AileArc.Development'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AileArc.Development'

function Enter-InstallerLock {
    $mutex = [Threading.Mutex]::new($false, 'Local\AileArc.Development.Installer')
    try { $entered = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $entered = $true }
    if (!$entered) { $mutex.Dispose(); throw 'Another AileArc installer or uninstaller is running.' }
    return $mutex
}

function Assert-PlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\' -or $full.TrimEnd('\').Length -lt 4) { throw "A local non-root path is required: $Path" }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not supported: $cursor" }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full.TrimEnd('\')
}
function Get-OwnedPath([string]$Root, [string]$Relative) {
    if (!$Relative -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '[:*?"<>|]' -or $Relative -match '(^|[\\/])(\.|\.\.)([\\/]|$)') { throw "Invalid receipt path: $Relative" }
    foreach ($part in ($Relative -split '[\\/]')) {
        if (!$part -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw "Invalid receipt path: $Relative" }
    }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Receipt path escaped its root.' }
    return Assert-PlainPath $path
}
function Read-Receipt([string]$Path) {
    $receipt = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.Product -ne $product -or $receipt.Schema -ne 1) { throw 'Unrecognized installation receipt.' }
    return $receipt
}
function Test-Hash([string]$Path, [string]$Hash) {
    if ($Hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid receipt hash.' }
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash
}
function Assert-AppStopped([string]$Root) {
    foreach ($process in @(Get-Process AileArc, AileArc.Worker -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Close AileArc and its tasks before installing or uninstalling.' }
    }
}
function Get-RegistryValue([string]$Path, [string]$Name) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    try { if ($key) { return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    finally { if ($key) { $key.Dispose() } }
    return $null
}
function Set-RegistryValue($Entry) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Entry.Path)
    try { $key.SetValue($Entry.Name, $Entry.Value, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}
function Remove-RegistryValue($Entry) {
    # Preserve any value subsequently changed by the user or another installer.
    if ((Get-RegistryValue $Entry.Path $Entry.Name) -ceq $Entry.Value) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Entry.Path, $true)
        try { $key.DeleteValue($Entry.Name, $false) } finally { $key.Dispose() }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Entry.Path)
        try { $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0 } finally { $key.Dispose() }
        if ($empty) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($Entry.Path, $false) }
    }
}
function Remove-OwnedFiles([string]$Root, $Files) {
    foreach ($file in $Files) {
        $path = Get-OwnedPath $Root $file.Path
        if (Test-Hash $path $file.Sha256) {
            try { Remove-Item -LiteralPath $path -Force } catch { Write-Warning "Retained occupied file: $path" }
        } elseif (Test-Path -LiteralPath $path) { Write-Warning "Retained modified file: $path" }
    }
    # Only directories named by the receipt, and only when empty. No recursive deletion.
    $directories = @($Files | ForEach-Object {
        $parent = [IO.Path]::GetDirectoryName((Get-OwnedPath $Root $_.Path))
        while ($parent -and $parent.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
            $parent
            if ($parent -eq $Root) { break }
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
    } | Sort-Object -Unique | Sort-Object Length -Descending)
    foreach ($directory in $directories) {
        if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory }
    }
}
function Update-Shell {
    if (!('AileArcInstaller.Native' -as [type])) {
        Add-Type -TypeDefinition 'namespace AileArcInstaller { public static class Native { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
    }
    [AileArcInstaller.Native]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
}

function New-StartMenuShortcut([string]$Path, [string]$Target, [string]$Directory) {
    # Use the Unicode shell interface directly. WScript.Shell TargetPath fails on some
    # Windows Server/locale combinations for the same non-ASCII path that exists on disk.
    if (!('AileArcInstaller.Shortcut' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace AileArcInstaller {
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    public static class Shortcut {
        public static void Save(string path, string target, string directory) {
            var link = (IShellLinkW)new ShellLink();
            try {
                link.SetPath(target);
                link.SetWorkingDirectory(directory);
                link.SetDescription("AileArc Development");
                ((IPersistFile)link).Save(path, true);
            } finally { Marshal.FinalReleaseComObject(link); }
        }
        public static string ReadTarget(string path) {
            var link = (IShellLinkW)new ShellLink();
            try {
                ((IPersistFile)link).Load(path, 0);
                var value = new StringBuilder(32768);
                link.GetPath(value, value.Capacity, IntPtr.Zero, 4);
                return value.ToString();
            } finally { Marshal.FinalReleaseComObject(link); }
        }
    }
}
'@
    }
    [AileArcInstaller.Shortcut]::Save($Path, $Target, $Directory)
    if ([AileArcInstaller.Shortcut]::ReadTarget($Path) -ine $Target) { throw 'Start Menu shortcut target did not round-trip correctly.' }
}
