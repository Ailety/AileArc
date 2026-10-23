#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$BundleDirectory, [string]$PreviousBundleDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
. (Join-Path $bundleRoot 'Install.Common.ps1')
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
if ($null -ne (Get-RegistryValue $uninstallKey 'InstallLocation')) { throw 'Smoke test requires no existing AileArc development installation; it will not replace one.' }
$testRoot = Join-Path $taskRoot ('.artifacts/install-test-' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot '自定义 安装目录'
$ps5 = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
function Snapshot-Defaults {
    $result = @{}
    foreach ($extension in @('.zip','.7z','.rar')) {
        $result[$extension] = @(
            (Get-RegistryValue "Software\Classes\$extension" ''),
            (Get-RegistryValue "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice" 'ProgId'),
            (Get-RegistryValue "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice" 'Hash'))
    }
    return $result | ConvertTo-Json -Compress
}
$defaults = Snapshot-Defaults
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
foreach ($bad in @('..\outside','C:\outside','file:ads','dir\NUL.txt','dir\trailing.')) {
    $rejected = $false
    try { $null = Get-OwnedPath $testRoot $bad } catch { $rejected = $true }
    Assert $rejected "Unsafe receipt path accepted: $bad"
}
$foreign = Join-Path $testRoot 'unowned'
New-Item -ItemType Directory -Path $foreign | Out-Null
Set-Content -LiteralPath (Join-Path $foreign 'keep.txt') -Value 'keep' -Encoding utf8
& $ps5 -NoProfile -NonInteractive -File (Join-Path $bundleRoot 'Install.ps1') -InstallDirectory $foreign 2>&1 | Out-Null
Assert ($LASTEXITCODE -ne 0) 'Nonempty foreign folder should be refused.'
Assert (Test-Path -LiteralPath (Join-Path $foreign 'keep.txt')) 'Foreign file was removed.'
try {
    $initialBundle = if ($PreviousBundleDirectory) { [IO.Path]::GetFullPath($PreviousBundleDirectory) } else { $bundleRoot }
    & $ps5 -NoProfile -NonInteractive -File (Join-Path $initialBundle 'Install.ps1') -InstallDirectory $installRoot
    Assert ($LASTEXITCODE -eq 0) 'Initial installation failed.'
    $first = Read-Receipt (Join-Path $installRoot '.ailearc-install.json')
    $firstVersion = Join-Path $installRoot ('versions/' + $first.Current)
    $firstApp = Join-Path $firstVersion 'AileArc.exe'
    Assert (Test-Path -LiteralPath $firstApp) 'App executable missing.'
    $command = Get-RegistryValue 'Software\Classes\AileArc.Development.Archive\shell\open\command' ''
    Assert ($command -ceq ('"' + $firstApp + '" --open -- "%1"')) 'Open With command incorrectly quoted.'
    Assert ((Snapshot-Defaults) -ceq $defaults) 'Installation modified a user default.'
    & (Join-Path $PSScriptRoot 'Test-PackageWorker.ps1') -WorkerExecutable (Join-Path $firstVersion 'Worker/AileArc.Worker.exe')
    # A late failure after registry/shortcut updates must restore the old installation.
    $receiptPath = Join-Path $installRoot '.ailearc-install.json'
    $receiptLock = [IO.File]::Open($receiptPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        & $ps5 -NoProfile -NonInteractive -File (Join-Path $bundleRoot 'Install.ps1') -InstallDirectory $installRoot 2>&1 | Out-Null
        Assert ($LASTEXITCODE -ne 0) 'A locked receipt should prevent upgrade commit.'
    } finally { $receiptLock.Dispose() }
    Assert ((Read-Receipt $receiptPath).Current -eq $first.Current) 'Failed commit changed the active version.'
    Assert ((Get-RegistryValue 'Software\Classes\AileArc.Development.Archive\shell\open\command' '') -ceq $command) 'Failed commit did not restore the old launch command.'
    Assert (Test-Hash $first.Shortcut.Path $first.Shortcut.Sha256) 'Failed commit did not restore the old shortcut.'
    # Integrity failures must not update the current installed version or registrations.
    $manifestPath = Join-Path $bundleRoot 'bundle.json'
    $originalManifest = [IO.File]::ReadAllBytes($manifestPath)
    try {
        $badBundle = Read-Receipt $manifestPath
        $badBundle.Files[0].Sha256 = '0' * 64
        $badBundle | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        & $ps5 -NoProfile -NonInteractive -File (Join-Path $bundleRoot 'Install.ps1') -InstallDirectory $installRoot 2>&1 | Out-Null
        Assert ($LASTEXITCODE -ne 0) 'Damaged bundle was accepted.'
        Assert ((Read-Receipt (Join-Path $installRoot '.ailearc-install.json')).Current -eq $first.Current) 'Failed upgrade changed receipt.'
    } finally { [IO.File]::WriteAllBytes($manifestPath, $originalManifest) }
    & $ps5 -NoProfile -NonInteractive -File (Join-Path $bundleRoot 'Install.ps1') -InstallDirectory $installRoot
    Assert ($LASTEXITCODE -eq 0) 'Upgrade failed.'
    $second = Read-Receipt (Join-Path $installRoot '.ailearc-install.json')
    Assert ($second.Current -ne $first.Current -and $second.Versions.Count -eq 2) 'Upgrade did not stage a new version.'
    $secondVersion = Join-Path $installRoot ('versions/' + $second.Current)
    Assert (Test-Path -LiteralPath (Join-Path $secondVersion 'PresentationFramework.dll')) 'WPF runtime missing after upgrade.'
    foreach ($legacyFile in @('Microsoft.UI.Xaml.dll','App.xbf','AileArc.pri','Microsoft.Web.WebView2.Core.dll')) {
        Assert (!(Test-Path -LiteralPath (Join-Path $secondVersion $legacyFile))) "Legacy UI file shipped: $legacyFile"
    }
    $secondApp = Join-Path $secondVersion 'AileArc.exe'
    Assert ((Get-RegistryValue 'Software\Classes\AileArc.Development.Archive\shell\open\command' '') -ceq ('"' + $secondApp + '" --open -- "%1"')) 'Upgrade did not select the WPF executable.'
    foreach ($language in @('zh-CN','en-US')) {
        $client = Start-Process -FilePath $secondApp -ArgumentList @('--self-test',$language) -WindowStyle Hidden -PassThru
        if (!$client.WaitForExit(90000)) { $client.Kill($true); throw "Installed client timed out: $language" }
        Assert ($client.ExitCode -eq 0) "Installed WPF client failed: $language"
    }
    $extra = Join-Path $secondVersion 'user-note.txt'
    $modified = Join-Path $secondVersion 'LICENSE'
    Set-Content -LiteralPath $extra -Value 'User-added content' -Encoding utf8
    Set-Content -LiteralPath $modified -Value 'User-modified content' -Encoding utf8
    & $ps5 -NoProfile -NonInteractive -File (Join-Path $secondVersion 'Uninstall.ps1') -InstallDirectory $installRoot
    Assert ($LASTEXITCODE -eq 0) 'Uninstall failed.'
    Assert (Test-Path -LiteralPath $extra) 'Uninstall removed an additional file.'
    Assert ((Get-Content -LiteralPath $modified -Raw -Encoding utf8).Trim() -eq 'User-modified content') 'Uninstall removed a modified file.'
    Assert (!(Test-Path -LiteralPath $firstApp)) 'Uninstall left an unchanged old executable.'
    Assert ($null -eq (Get-RegistryValue $uninstallKey 'InstallLocation')) 'Uninstall registration remains.'
    Assert ($null -eq (Get-RegistryValue 'Software\Classes\AileArc.Development.Archive\shell\open\command' '')) 'Open With launch command remains.'
    Assert ($null -eq (Get-RegistryValue 'Software\RegisteredApplications' 'AileArc.Development')) 'Registered application remains.'
    Assert ((Snapshot-Defaults) -ceq $defaults) 'Upgrade/uninstall changed user defaults.'
    Write-Host 'Installer tests passed: paths, integrity, custom directory, WPF/Shell action checks in both languages, Open With, upgrade, uninstall, defaults and file preservation.'
} finally {
    if (Test-Path -LiteralPath (Join-Path $installRoot '.ailearc-install.json')) {
        $remaining = Read-Receipt (Join-Path $installRoot '.ailearc-install.json')
        & $ps5 -NoProfile -NonInteractive -File (Join-Path $installRoot ('versions/' + $remaining.Current + '/Uninstall.ps1')) -InstallDirectory $installRoot
    }
}
