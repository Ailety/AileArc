#requires -Version 7.0
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][ValidateSet('zh-CN','en-US')][string]$Language)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test',$Language) -WindowStyle Hidden -PassThru
try {
    # This is the deadline for the entire window suite. Individual operations have their own timeouts.
    if (!$process.WaitForExit(180000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "WPF client suite exceeded three minutes: $Language ($exe)"
    }
    if ($process.ExitCode -ne 0) {
        $log = Join-Path (Split-Path -Parent $exe) 'client-self-test-error.txt'
        $details = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw -Encoding utf8 }
        throw "WPF client suite failed: $Language ($exe), exit $($process.ExitCode). $details"
    }
    Write-Host "WPF client self-test passed: $Language ($([math]::Round($watch.Elapsed.TotalSeconds,1)) s)"
} finally { $process.Dispose() }
