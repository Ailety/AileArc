#requires -Version 7.0
param([string]$Archive)
$ErrorActionPreference = 'Stop'
$output = Join-Path (Split-Path -Parent $PSScriptRoot) '.artifacts/native-validation'
$native = Join-Path $output 'ailearc-native.exe'
if (!(Test-Path -LiteralPath $native)) { throw 'Build-NativeValidation.ps1 must succeed first.' }
if ($Archive) {
    $source = (Resolve-Path -LiteralPath $Archive).Path
    $log = Join-Path $output ('logs/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
    Start-Process -FilePath (Join-Path $output 'backend/AileArc.NativeValidation.exe') -WorkingDirectory $output -WindowStyle Hidden -ArgumentList @('"' + $native + '"', '"' + $source + '"') -RedirectStandardOutput "$log.stdout.txt" -RedirectStandardError "$log.stderr.txt"
} else { Start-Process -FilePath $native -WorkingDirectory $output -WindowStyle Hidden }
