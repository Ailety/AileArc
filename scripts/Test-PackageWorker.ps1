#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$WorkerExecutable)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$worker = [IO.Path]::GetFullPath($WorkerExecutable)
if (!(Test-Path -LiteralPath $worker)) { throw 'Published Worker executable missing.' }
$previous = $env:AILEARC_TEST_WORKER
try {
    $env:AILEARC_TEST_WORKER = $worker
    & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') test (Join-Path $taskRoot 'tests/AileArc.Tests/AileArc.Tests.csproj') -c Release -p:RestoreLockedMode=true -v minimal --filter 'FullyQualifiedName~NativeZipScanPreservesPathsAndDuplicateIds|FullyQualifiedName~ExtractsVerifiedContentAndPreservesStructure|FullyQualifiedName~CreatedArchivesRoundTripUnicodeEmptyFoldersAndPassword'
    if ($LASTEXITCODE -ne 0) { throw 'Installed Worker integration tests failed.' }
} finally { $env:AILEARC_TEST_WORKER = $previous }
