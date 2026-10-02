#requires -Version 5.1
param([ValidateSet('Register','Remove')][string]$Mode, [string]$InstallDirectory, [string]$ExpectedPackageFullName = '')
$ErrorActionPreference = 'Stop'
try {
    $existing = @(Get-AppxPackage -Name 'Ailety.AileArc.Development')
    if ($existing.Count -gt 0 -and (!$ExpectedPackageFullName -or $existing[0].PackageFullName -ne $ExpectedPackageFullName)) { throw 'An unowned AileArc identity package is already registered.' }
    if ($Mode -eq 'Remove') {
        foreach ($item in $existing) { Remove-AppxPackage -Package $item.PackageFullName -ErrorAction Stop }
    } else {
        if ($existing.Count -gt 0) { throw 'Remove the previous development identity before registering a new external location.' }
        $packagePath = Join-Path $InstallDirectory 'AileArc.Identity.msix'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $reader = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        $unsigned = $manifest.Package.Identity.Publisher -match 'OID.2.25.311729368913984317654407730594956997722=1'
        Add-AppxPackage -Path $packagePath -ExternalLocation $InstallDirectory -AllowUnsigned:$unsigned -ErrorAction Stop
        (Get-AppxPackage -Name 'Ailety.AileArc.Development').PackageFullName | Write-Output
    }
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
