#requires -Version 7.0
param([string]$Archive, [ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$app = Join-Path $taskRoot "src/AileArc.UI/bin/$Configuration/net10.0-windows10.0.22621.0/win-x64/AileArc.exe"
if (-not (Test-Path -LiteralPath $app)) { throw 'Run scripts/Build.ps1 first.' }
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools/dotnet'
$start = [Diagnostics.ProcessStartInfo]::new($app)
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
if ($Archive) { $start.ArgumentList.Add((Resolve-Path -LiteralPath $Archive).Path) }
[Diagnostics.Process]::Start($start) | Select-Object Id,ProcessName
