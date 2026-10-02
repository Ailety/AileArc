#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$InstallDirectory)
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
$installerLock = Enter-InstallerLock
try {
$root = Assert-PlainPath $InstallDirectory
$receiptPath = Get-OwnedPath $root '.ailearc-install.json'
$receipt = Read-Receipt $receiptPath
if ($receipt.Root -ne $root) { throw 'Installation receipt belongs to another directory.' }
Assert-AppStopped $root
# Validate the entire receipt before removing anything.
foreach ($version in $receipt.Versions) {
    if ($version.Id -notmatch '^[a-f0-9]{32}$') { throw 'Invalid version directory.' }
    $directory = Get-OwnedPath $root ('versions\' + $version.Id)
    foreach ($file in $version.Files) { $null = Get-OwnedPath $directory $file.Path; if ($file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid file hash.' } }
}
if ($receipt.PackageFullName) {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Register-Shell.ps1') -Mode Remove -ExpectedPackageFullName $receipt.PackageFullName
    if ($LASTEXITCODE -ne 0) { throw 'Modern-menu removal failed; installation retained.' }
}
foreach ($entry in @($receipt.Registry | Sort-Object { $_.Path.Length } -Descending)) { Remove-RegistryValue $entry }
$expectedShortcut = Assert-PlainPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'AileArc Development.lnk')
if ($receipt.Shortcut.Path -eq $expectedShortcut -and (Test-Hash $expectedShortcut $receipt.Shortcut.Sha256)) { Remove-Item -LiteralPath $expectedShortcut }
foreach ($version in $receipt.Versions) { Remove-OwnedFiles (Get-OwnedPath $root ('versions\' + $version.Id)) $version.Files }
Remove-Item -LiteralPath $receiptPath
foreach ($directory in @((Join-Path $root 'versions'), $root)) {
    if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory }
}
Update-Shell
Write-Host 'AileArc development registration removed. User settings, work copies, and modified or additional files are retained.'
} finally { $installerLock.ReleaseMutex(); $installerLock.Dispose() }
