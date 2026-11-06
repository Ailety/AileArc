#requires -Version 7.0
param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $root '.artifacts/client/Release/AileArc.exe' }
$exe = (Resolve-Path -LiteralPath $Executable).Path
$profileDir = Join-Path $root ('.artifacts/language-restart-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($profileDir)
if (!('ClientMeasurementJob' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ClientMeasurementJob.cs') }
$job = [ClientMeasurementJob]::new()
$parent = [Diagnostics.Process]::new()
$started = $false
try {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardInput = $true
    foreach ($argument in '--self-test','language-restart',$profileDir) { $start.ArgumentList.Add($argument) }
    $parent.StartInfo = $start; $started = $parent.Start()
    if (!$started) { throw 'Could not start the isolated language session.' }
    $job.Attach($parent); $parent.StandardInput.WriteLine('start'); $parent.StandardInput.Close()
    $reportPath = Join-Path $profileDir 'restored.json'
    $failure = Join-Path $profileDir 'failure.txt'
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $reportPath)) {
        if (Test-Path -LiteralPath $failure) { throw (Get-Content -Raw -Encoding utf8 -LiteralPath $failure) }
        if (($parent.HasExited -and $parent.ExitCode -ne 0) -or $wait.Elapsed.TotalSeconds -gt 60) { throw 'Replacement did not restore the test session.' }
        Start-Sleep -Milliseconds 50
    }
    $report = Get-Content -Raw -Encoding utf8 -LiteralPath $reportPath | ConvertFrom-Json
    $english = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $profileDir 'english.json') | ConvertFrom-Json
    $saved = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $profileDir 'settings.json') | ConvertFrom-Json
    if (!$parent.HasExited -or $parent.ExitCode -ne 0 -or $parent.Id -eq $report.pid -or
        $english.pid -eq $parent.Id -or $english.pid -eq $report.pid -or $english.language -ne 'en-US' -or
        $report.language -ne 'zh-CN' -or $saved.Language -ne $report.language -or $english.windows -ne 3 -or $report.windows -ne 3 -or
        ($report.codePages -join ',') -ne '936,65001' -or ($english.codePages -join ',') -ne '936,65001') { throw 'Replacement session or persisted preference differs.' }
    [void]$job.TotalProcesses() # Ensures the child client and its workers have exited.
    Write-Host "Restart verified: $($parent.Id) -> $($english.pid) -> $($report.pid); three windows, zh-CN/en-US/zh-CN, ZIP code pages 936/65001."
} finally {
    $job.Dispose()
    if ($started -and !$parent.HasExited) { $parent.Kill($true); $parent.WaitForExit() }
    $parent.Dispose()
}
