#requires -Version 7.0
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location -LiteralPath $taskRoot
try {
    & $dotnet build 'src/AileArc.UI/AileArc.UI.csproj' -c $Configuration -p:RestoreLockedMode=true -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed.' }
    if (-not $SkipTests) {
        & $dotnet test 'tests/AileArc.Tests/AileArc.Tests.csproj' -c $Configuration -p:RestoreLockedMode=true -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
} finally { Pop-Location }
