#requires -Version 7.0
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$Run, [string]$Archive = '', [switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$frontend = Join-Path $taskRoot 'prototypes/ailearc-ui'
Push-Location -LiteralPath $frontend
try {
    if (!$SkipRestore) {
        & npm install --prefer-offline --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Desktop frontend restore failed. Close the prototype preview server before retrying dependency changes.' }
    }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Desktop frontend build failed.' }
} finally { Pop-Location }
$output = Join-Path $taskRoot ".artifacts/desktop-validation/$Configuration"
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.Desktop/AileArc.Desktop.csproj') -c $Configuration -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $output -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Desktop host build failed.' }
& $dotnet publish (Join-Path $taskRoot 'src/AileArc.Worker/AileArc.Worker.csproj') -c $Configuration -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $output 'Worker') -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
New-Item -ItemType Directory -Path (Join-Path $output 'Web'),(Join-Path $output 'Worker') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $frontend 'dist/client') | Copy-Item -Destination (Join-Path $output 'Web') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $output
$notices = Join-Path $output 'Notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/dotnet/LICENSE.txt') -Destination (Join-Path $notices 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/dotnet/ThirdPartyNotices.txt') -Destination (Join-Path $notices 'dotnet-ThirdPartyNotices.txt')
foreach ($package in @('react','react-dom','@phosphor-icons/react')) {
    $license = Join-Path $frontend "node_modules/$package/LICENSE"
    Copy-Item -LiteralPath $license -Destination (Join-Path $notices ($package.Replace('/','-').TrimStart('@') + '-LICENSE.txt'))
}
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
Get-ChildItem -LiteralPath (Join-Path $packageRoot 'microsoft.web.webview2/1.0.4258.31') -File | Where-Object Name -Match '^(license|notice)' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices ('WebView2-' + $_.Name))
}
Write-Host "Desktop validation: $output"
if ($Run) {
    $arguments = @()
    if ($Archive) { $arguments = @('"' + [IO.Path]::GetFullPath($Archive) + '"') }
    if ($arguments.Count) { Start-Process -FilePath (Join-Path $output 'AileArc.Desktop.Validation.exe') -ArgumentList $arguments -WindowStyle Hidden }
    else { Start-Process -FilePath (Join-Path $output 'AileArc.Desktop.Validation.exe') -WindowStyle Hidden }
}
