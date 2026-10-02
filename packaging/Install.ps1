#requires -Version 5.1
param([string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\AileArc-Development'), [switch]$EnableModernMenu)
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
$installerLock = Enter-InstallerLock
try {
if (![Environment]::Is64BitProcess -or [Environment]::OSVersion.Version.Build -lt 22621) { throw 'Windows 11 x64 is required; use 64-bit PowerShell.' }
$root = Assert-PlainPath $InstallDirectory
$receiptPath = Join-Path $root '.ailearc-install.json'
$old = $null
if (Test-Path -LiteralPath $receiptPath) { $old = Read-Receipt $receiptPath }
elseif ((Test-Path -LiteralPath $root) -and @(Get-ChildItem -LiteralPath $root -Force).Count) { throw 'Choose a new or empty installation folder.' }
$registeredRoot = Get-RegistryValue $uninstallKey 'InstallLocation'
if ($null -ne $registeredRoot -and (!$old -or $registeredRoot -ne $root)) { throw "An AileArc development installation is already registered at $registeredRoot" }
if ($old -and $old.Root -ne $root) { throw 'Installation receipt belongs to another directory.' }
Assert-AppStopped $root
$bundle = Read-Receipt (Join-Path $PSScriptRoot 'bundle.json')
$source = Assert-PlainPath (Join-Path $PSScriptRoot 'payload')
$seen = @{}
foreach ($file in $bundle.Files) {
    if ($seen.ContainsKey($file.Path)) { throw 'Duplicate bundle path.' }
    $seen[$file.Path] = $true
    if (!(Test-Hash (Get-OwnedPath $source $file.Path) $file.Sha256)) { throw "Bundle verification failed: $($file.Path)" }
}
foreach ($required in @('AileArc.exe','Worker\AileArc.Worker.exe','Worker\7z.dll','Shell\AileArc.Shell.dll','AileArc.Identity.msix')) {
    if (!$seen.ContainsKey($required)) { throw "Bundle is incomplete: $required" }
}
$id = [guid]::NewGuid().ToString('N')
$version = Get-OwnedPath $root "versions\$id"
$app = Join-Path $version 'AileArc.exe'
$uninstaller = Join-Path $version 'Uninstall.ps1'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$entries = [Collections.Generic.List[object]]::new()
function Add-Entry([string]$Path, [string]$Name, [string]$Value) { $entries.Add([pscustomobject]@{ Path=$Path; Name=$Name; Value=$Value }) }
$progId = 'Software\Classes\AileArc.Development.Archive'
Add-Entry $progId '' 'AileArc archive'
Add-Entry "$progId\shell\open\command" '' ('"' + $app + '" --open -- "%1"')
foreach ($extension in @('.zip','.7z','.rar')) {
    Add-Entry "Software\Classes\$extension\OpenWithProgids" 'AileArc.Development.Archive' ''
    Add-Entry 'Software\Ailety\AileArc\Capabilities\FileAssociations' $extension 'AileArc.Development.Archive'
}
Add-Entry 'Software\Ailety\AileArc\Capabilities' 'ApplicationName' 'AileArc Development'
Add-Entry 'Software\Ailety\AileArc\Capabilities' 'ApplicationDescription' 'AileArc archive manager'
Add-Entry 'Software\RegisteredApplications' 'AileArc.Development' 'Software\Ailety\AileArc\Capabilities'
Add-Entry $uninstallKey 'DisplayName' 'AileArc Development'
Add-Entry $uninstallKey 'Publisher' 'Ailety'
Add-Entry $uninstallKey 'DisplayVersion' '0.1 (development)'
Add-Entry $uninstallKey 'InstallLocation' $root
Add-Entry $uninstallKey 'UninstallString' ('"' + $powershell + '" -NoProfile -File "' + $uninstaller + '" -InstallDirectory "' + $root + '"')
foreach ($entry in $entries) {
    $actual = Get-RegistryValue $entry.Path $entry.Name
    $previous = @()
    if ($old) { $previous = @($old.Registry | Where-Object { $_.Path -eq $entry.Path -and $_.Name -eq $entry.Name -and $_.Value -ceq $actual }) }
    if ($null -ne $actual -and !$previous.Count) { throw "Registry value is not owned by this installation: $($entry.Path) / $($entry.Name)" }
}
$shortcut = Assert-PlainPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'AileArc Development.lnk')
if ((Test-Path -LiteralPath $shortcut) -and (!$old -or $old.Shortcut.Path -ne $shortcut -or !(Test-Hash $shortcut $old.Shortcut.Sha256))) { throw 'An unowned Start Menu shortcut already exists.' }
if ($old -and $old.PackageFullName) { throw 'Uninstall the previous modern-menu development installation before updating it; package-location rollback is not yet supported.' }
$installedFiles = [Collections.Generic.List[object]]::new()
$shortcutBefore = $null
if (Test-Path -LiteralPath $shortcut) { $shortcutBefore = [IO.File]::ReadAllBytes($shortcut) }
$shortcutWritten = $false
$packageName = ''
$committed = $false
$temporary = ''
try {
    New-Item -ItemType Directory -Path $version -Force | Out-Null
    foreach ($file in $bundle.Files) {
        $target = Get-OwnedPath $version $file.Path
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath (Get-OwnedPath $source $file.Path) -Destination $target
        $installedFiles.Add($file)
        if (!(Test-Hash $target $file.Sha256)) { throw 'Installed file verification failed.' }
    }
    foreach ($name in @('Uninstall.ps1','Install.Common.ps1','Register-Shell.ps1')) {
        $target = Get-OwnedPath $version $name
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $target
        $installedFiles.Add([pscustomobject]@{ Path=$name; Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash })
    }
    if ($EnableModernMenu) {
        $result = & $powershell -NoProfile -NonInteractive -File (Join-Path $version 'Register-Shell.ps1') -Mode Register -InstallDirectory $version
        if ($LASTEXITCODE -ne 0) { throw 'Windows declined modern-menu registration. No security settings were changed. Use a package signed by an already trusted certificate, or retry without -EnableModernMenu for Open With installation.' }
        $packageName = @($result)[-1].ToString().Trim()
    }
    foreach ($entry in $entries) { Set-RegistryValue $entry }
    $shortcutWritten = $true
    New-StartMenuShortcut $shortcut $app $version
    $versions = @()
    if ($old) { $versions = @($old.Versions) }
    $versions += [pscustomobject]@{ Id=$id; Files=@($installedFiles.ToArray()) }
    $receipt = [ordered]@{ Product=$product; Schema=1; Root=$root; Current=$id; Versions=$versions; Registry=@($entries.ToArray()); Shortcut=@{ Path=$shortcut; Sha256=(Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash }; PackageFullName=$packageName }
    $temporary = Join-Path $root ('.receipt-' + $id + '.tmp')
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    if ($old) { [IO.File]::Replace($temporary, $receiptPath, [NullString]::Value) } else { [IO.File]::Move($temporary, $receiptPath) }
    $committed = $true
    Update-Shell
    Write-Host "Installed: $app"
    Write-Host ('Modern context menu registered: ' + [bool]$packageName)
    Write-Host 'Default file associations were not changed. Choose AileArc in Windows Open With or Default Apps.'
} finally {
    if (!$committed) {
        foreach ($entry in $entries) { Remove-RegistryValue $entry }
        if ($old) { foreach ($entry in $old.Registry) { Set-RegistryValue $entry } }
        if ($shortcutWritten) {
            if ($null -ne $shortcutBefore) { [IO.File]::WriteAllBytes($shortcut, $shortcutBefore) }
            elseif (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
        }
        if ($packageName) { & $powershell -NoProfile -NonInteractive -File (Join-Path $version 'Register-Shell.ps1') -Mode Remove -ExpectedPackageFullName $packageName }
        Remove-OwnedFiles $version $installedFiles
        if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary }
    }
}
} finally { $installerLock.ReleaseMutex(); $installerLock.Dispose() }
