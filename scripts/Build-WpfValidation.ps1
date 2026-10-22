#requires -Version 7.0
param([switch]$Run, [string]$Archive = '', [switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$output = Join-Path $taskRoot '.artifacts/wpf-validation/Release'
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.WpfValidation/AileArc.WpfValidation.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $output -v minimal
if ($LASTEXITCODE -ne 0) { throw 'WPF validation build failed.' }
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.Worker/AileArc.Worker.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $output 'Worker') -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $output
$notices = Join-Path $output 'Notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/dotnet/LICENSE.txt'),(Join-Path $taskRoot '.tools/dotnet/ThirdPartyNotices.txt') -Destination $notices
$exe = Join-Path $output 'AileArc.Wpf.Validation.exe'
if ($Test) {
    $check = Start-Process -FilePath $exe -ArgumentList '--self-test' -WorkingDirectory $output -WindowStyle Hidden -PassThru
    if (!$check.WaitForExit(60000)) { $check.Kill($true); throw 'WPF self-test timed out.' }
    if ($check.ExitCode -ne 0) { throw "WPF self-test failed; inspect $output/self-test-error.txt" }
    Write-Host 'WPF actual-control self-test passed: virtualization, selection, mixed state and replacement.'
}
Write-Host "WPF validation output: $output"
if ($Run) {
    if ($Archive) {
        $source = (Resolve-Path -LiteralPath $Archive).Path
        Start-Process -FilePath $exe -ArgumentList @('"' + $source + '"') -WorkingDirectory $output -WindowStyle Hidden
    } else { Start-Process -FilePath $exe -WorkingDirectory $output -WindowStyle Hidden }
}
