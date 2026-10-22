#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Native','WebView2','WPF')][string]$HostKind,
    [Parameter(Mandatory)][string]$Archive, [ValidateRange(10,60)][int]$Seconds = 20,
    [string]$Label = 'idle', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $Archive).Path
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot '.artifacts/ui-comparison' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($HostKind -eq 'Native') {
    $base = Join-Path $taskRoot '.artifacts/native-validation'
    $exe = Join-Path $base 'backend/AileArc.NativeValidation.exe'
    $arguments = @('"' + (Join-Path $base 'ailearc-native.exe') + '"', '"' + $source + '"')
} elseif ($HostKind -eq 'WPF') {
    $base = Join-Path $taskRoot '.artifacts/wpf-validation/Release'
    $exe = Join-Path $base 'AileArc.Wpf.Validation.exe'
    $arguments = @('"' + $source + '"')
} else {
    $base = Join-Path $taskRoot '.artifacts/desktop-validation/Release'
    $exe = Join-Path $base 'AileArc.Desktop.Validation.exe'
    $arguments = @('"' + $source + '"')
}
$started = Get-Date
$process = Start-Process -FilePath $exe -WorkingDirectory $base -ArgumentList $arguments -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $output "$HostKind-$stamp.stdout.txt") -RedirectStandardError (Join-Path $output "$HostKind-$stamp.stderr.txt")
$ownedIds = [Collections.Generic.HashSet[int]]::new()
[void]$ownedIds.Add($process.Id)
$samples = [Collections.Generic.List[object]]::new()
try {
    for ($second = 0; $second -lt $Seconds; $second++) {
        Start-Sleep -Seconds 1
        $inventory = @(Get-CimInstance Win32_Process | Where-Object { $_.CreationDate -ge $started.AddSeconds(-1) })
        do {
            $added = $false
            foreach ($item in $inventory) {
                if ($ownedIds.Contains([int]$item.ParentProcessId) -and $ownedIds.Add([int]$item.ProcessId)) { $added = $true }
            }
        } while ($added)
        $current = @($inventory | Where-Object { $ownedIds.Contains([int]$_.ProcessId) })
        $details = @($current | ForEach-Object {
            $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            if ($p) { [pscustomobject]@{ Id=$p.Id; Name=$p.ProcessName; PrivateBytes=$p.PrivateMemorySize64; WorkingSet=$p.WorkingSet64; CpuSeconds=$p.TotalProcessorTime.TotalSeconds } }
        })
        if (!$details.Count) { throw 'Measured process tree exited before sampling completed; inspect the error log.' }
        $samples.Add([pscustomobject]@{ ElapsedSeconds=((Get-Date)-$started).TotalSeconds; Processes=$details;
            PrivateBytes=($details | Measure-Object PrivateBytes -Sum).Sum; WorkingSet=($details | Measure-Object WorkingSet -Sum).Sum })
    }
} finally {
    # Only this invocation's tracked children; never enumerate and kill by process name.
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
}
$report = [pscustomobject]@{ HostKind=$HostKind; Label=$Label; Archive=(Split-Path -Leaf $source);
    ArchiveSHA256=(Get-FileHash -LiteralPath $source).Hash; Started=$started.ToString('o'); Samples=$samples;
    Notes='Wall-clock process sampling, not first-interactive timing. Working sets can double-count shared pages; private bytes are commit, not physical residency. Launches use warm filesystem caches.' }
$file = Join-Path $output "$HostKind-$Label-$stamp.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $file -Encoding utf8
Write-Host $file
