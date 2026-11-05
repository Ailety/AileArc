#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Archive,
    [ValidateSet('open','cancel')][string]$Mode = 'open',
    [ValidateRange(1,20)][int]$Runs = 3,
    [string]$Executable = '',
    [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $root '.artifacts/client/Release/AileArc.exe' }
$exe = (Resolve-Path -LiteralPath $Executable).Path
$sample = (Get-Item -LiteralPath $Archive)
if (!$Report) { $Report = Join-Path $root ".artifacts/client-measurements/$Mode-$([guid]::NewGuid().ToString('N')).json" }
$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path -LiteralPath $reportPath) { throw "Report already exists: $reportPath" }
if (!('ClientMeasurementJob' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ClientMeasurementJob.cs') }
$records = @(
    for ($i = 1; $i -le $Runs; $i++) {
        $job = [ClientMeasurementJob]::new()
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            $start = [Diagnostics.ProcessStartInfo]::new($exe)
            $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
            $start.RedirectStandardInput = $start.RedirectStandardOutput = $start.RedirectStandardError = $true
            foreach ($arg in '--self-test','measure',$Mode,$sample.FullName) { $start.ArgumentList.Add($arg) }
            $process.StartInfo = $start
            $start.ArgumentList.Add([Diagnostics.Stopwatch]::GetTimestamp().ToString([Globalization.CultureInfo]::InvariantCulture))
            $started = $process.Start()
            if (!$started) { throw 'Could not start the client.' }
            $job.Attach($process)
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            $process.StandardInput.WriteLine('start'); $process.StandardInput.Close()
            $timer = [Diagnostics.Stopwatch]::StartNew()
            [long]$peakWs = 0
            do {
                $peakWs = [Math]::Max($peakWs, $job.SampleWorkingSet())
                if ($timer.Elapsed.TotalSeconds -gt 90) { throw 'Client measurement timed out.' }
            } while (!$process.WaitForExit(20))
            $message = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw "Client measurement failed ($($process.ExitCode)): $message" }
            $row = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable
            if ($row.mode -ne $Mode -or $peakWs -le 0) { throw 'Incomplete client measurement.' }
            $row.run = $i
            $row.peakTreeCommitBytes = $job.PeakCommit()
            $row.processes = $job.TotalProcesses()
            $row.sampledProcesses = @($job.SampledProcessNames | Sort-Object)
            if ($row.processes -lt 2 -or $row.sampledProcesses -notcontains 'AileArc.Worker') { throw 'Did not observe the archive worker.' }
            $row.sampledPeakTreeWorkingSetBytes = $peakWs
            if ($row.peakTreeCommitBytes -le 0) { throw 'Missing job memory high-water mark.' }
            if ($Mode -eq 'open' -and ($row.pageRows -le 0 -or $null -eq $row.frameMs -or $row.frameMs -lt $row.boundMs -or
                $row.boundMs -lt $row.indexStartMs -or $row.indexStartMs -lt $row.scanStartMs)) { throw 'Invalid render milestones.' }
            if ($Mode -eq 'cancel' -and ($row.pageRows -ne 0 -or $row.entriesAtCancel -le 0 -or $null -eq $row.cancelMs)) { throw 'Invalid cancellation milestones.' }
            [pscustomobject]$row
        } finally {
            $job.Dispose()
            if ($started -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
)
$os = Get-CimInstance Win32_OperatingSystem
$median = [ordered]@{}
$metrics = @('peakTreeCommitBytes','sampledPeakTreeWorkingSetBytes') + $(if ($Mode -eq 'open') {
    'scanStartMs','indexStartMs','boundMs','frameMs'
} else { 'cancelAtMs','entriesAtCancel','cancelMs' })
foreach ($metric in $metrics) {
    $values = @($records | ForEach-Object { [double]$_.$metric } | Sort-Object)
    $middle = [int][Math]::Floor($values.Count / 2)
    $median[$metric] = if ($values.Count % 2 -eq 1) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
}
$result = [ordered]@{
    measuredUtc = [DateTime]::UtcNow.ToString('o'); os = $os.Caption; osBuild = $os.BuildNumber
    cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    physicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    clientSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $exe) 'AileArc.dll')).Hash
    workerSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $exe) 'Worker/AileArc.Worker.dll')).Hash
    coreSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $exe) 'AileArc.Core.dll')).Hash
    engineSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $exe) 'Worker/AileArc.Engine.dll')).Hash
    sample = $sample.Name; sampleBytes = $sample.Length; sampleSha256 = (Get-FileHash -LiteralPath $sample.FullName).Hash
    sampleIntervalMs = 20; median = $median; runs = $records
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath))
$result | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $reportPath -Encoding utf8 -NoClobber
$records | Format-Table run,mode,boundMs,frameMs,cancelMs,peakTreeCommitBytes,sampledPeakTreeWorkingSetBytes
Write-Host "Measurement report: $reportPath"
