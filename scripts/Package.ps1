#requires -Version 7.0
param([string]$OutputDirectory = '', [switch]$SkipTests,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SigningCertificateThumbprint = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('.artifacts/packages/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Package output must be a new directory.' }
& (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -SkipTests:$SkipTests
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$payload = Join-Path $OutputDirectory 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.UI/AileArc.UI.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $payload -v minimal
if ($LASTEXITCODE -ne 0) { throw 'UI publish failed.' }
foreach ($required in @('AileArc.pri', 'App.xbf', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Published UI resource missing: $required" }
}
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.Worker/AileArc.Worker.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $payload 'Worker') -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
& (Join-Path $PSScriptRoot 'Build-Shell.ps1') -OutputDirectory (Join-Path $payload 'Shell') -Test:(!$SkipTests)
$nativeNotices = Join-Path $payload 'Shell/Notices'
New-Item -ItemType Directory -Path $nativeNotices -Force | Out-Null
$nativeTool = Join-Path $taskRoot '.tools/llvm-mingw-20260922-ucrt-x86_64'
Copy-Item -LiteralPath (Join-Path $nativeTool 'LICENSE.TXT') -Destination (Join-Path $nativeNotices 'LLVM-LICENSE.TXT')
Get-ChildItem -LiteralPath (Join-Path $nativeTool 'x86_64-w64-mingw32/share/mingw32') -Filter 'COPYING*' | Copy-Item -Destination $nativeNotices
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'), (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $payload
$notices = Join-Path $payload 'Notices'
New-Item -ItemType Directory -Path (Join-Path $notices 'dotnet') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/dotnet/LICENSE.txt'), (Join-Path $taskRoot '.tools/dotnet/ThirdPartyNotices.txt') -Destination (Join-Path $notices 'dotnet')
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$dependencies = (Get-Content -LiteralPath (Join-Path $taskRoot 'src/AileArc.UI/packages.lock.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable).dependencies.Values
foreach ($target in $dependencies) {
    foreach ($entry in $target.GetEnumerator()) {
        if (!$entry.Value.resolved) { continue }
        $packageRoot = Join-Path $nugetRoot ($entry.Key.ToLowerInvariant() + '/' + $entry.Value.resolved)
        $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -ErrorAction SilentlyContinue | Where-Object Name -Match '(?i)^(license|notice|copying)')
        if ($licenseFiles.Count) {
            $destination = Join-Path $notices ($entry.Key + '-' + $entry.Value.resolved)
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            $licenseFiles | Copy-Item -Destination $destination
        }
    }
}
$identity = Join-Path $OutputDirectory 'identity-source'
New-Item -ItemType Directory -Path (Join-Path $identity 'Assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'packaging/Identity/AppxManifest.xml') -Destination $identity
if ($SigningCertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningCertificateThumbprint"
    if (!$certificate.HasPrivateKey) { throw 'The selected certificate has no private key.' }
    [xml]$manifestXml = Get-Content -LiteralPath (Join-Path $identity 'AppxManifest.xml') -Raw -Encoding utf8
    $manifestXml.Package.Identity.Publisher = $certificate.Subject
    $manifestXml.Save((Join-Path $identity 'AppxManifest.xml'))
}
# Simple code-drawn development icon; no downloaded artwork.
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(150, 150)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(42, 89, 170))
try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.FillRectangle($brush, 10, 25, 130, 110)
    $graphics.FillRectangle([Drawing.Brushes]::White, 66, 25, 18, 80)
    $bitmap.Save((Join-Path $identity 'Assets/Logo.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally { $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
$sdkTools = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools/10.0.26100.4654/bin/10.0.26100.0/x64/makeappx.exe'
if (!(Test-Path -LiteralPath $sdkTools)) { throw 'Pinned Windows SDK MakeAppx tool not found after UI restore.' }
& $sdkTools pack /o /nv /d $identity /p (Join-Path $payload 'AileArc.Identity.msix')
if ($LASTEXITCODE -ne 0) { throw 'Identity package creation failed.' }
if ($SigningCertificateThumbprint) {
    & (Join-Path (Split-Path -Parent $sdkTools) 'signtool.exe') sign /sha1 $SigningCertificateThumbprint /s My /fd SHA256 (Join-Path $payload 'AileArc.Identity.msix')
    if ($LASTEXITCODE -ne 0) { throw 'Identity package signing failed.' }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'packaging/Install.ps1'), (Join-Path $taskRoot 'packaging/Uninstall.ps1'), (Join-Path $taskRoot 'packaging/Install.Common.ps1'), (Join-Path $taskRoot 'packaging/Register-Shell.ps1') -Destination $OutputDirectory
# Receipts contain every shipped file; uninstall only removes unchanged owned files.
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($payload, $_.FullName); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{ Product = 'AileArc.Development'; Schema = 1; Files = $files }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bundle.json') -Encoding utf8
Write-Host "Development bundle: $OutputDirectory"
