#requires -Version 7.0
param([switch]$Run, [switch]$Test, [string]$Archive = '', [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$output = Join-Path $taskRoot ".artifacts/client/$Configuration"
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.Client/AileArc.Client.csproj') -c $Configuration -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $output -v minimal
if ($LASTEXITCODE -ne 0) { throw 'WPF client publish failed.' }
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.Worker/AileArc.Worker.csproj') -c $Configuration -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $output 'Worker') -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $output
$notices = Join-Path $output 'Notices'; New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/dotnet/LICENSE.txt'),(Join-Path $taskRoot '.tools/dotnet/ThirdPartyNotices.txt') -Destination $notices
$exe = Join-Path $output 'AileArc.exe'
if ($Test) {
    foreach ($language in @('zh-CN','en-US')) {
        & (Join-Path $PSScriptRoot 'Test-ClientWindow.ps1') -Executable $exe -Language $language
    }
}
if ($Run) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe); $start.UseShellExecute=$false; $start.WindowStyle='Hidden'
    if ($Archive) { $start.ArgumentList.Add((Resolve-Path -LiteralPath $Archive).Path) }
    [void][Diagnostics.Process]::Start($start)
}
Write-Host "WPF client: $exe"
