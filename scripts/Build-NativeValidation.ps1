#requires -Version 7.0
param([string]$NodePath = 'node', [string]$PythonPath = 'python', [switch]$SkipBootstrap)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $taskRoot '.tools/native-sdk-source'
$prototype = Join-Path $taskRoot 'prototypes/ailearc-native'
$node = (Get-Command $NodePath -ErrorAction Stop).Source
if ([int]((& $node --version).TrimStart('v').Split('.')[0]) -lt 24) { throw 'Native SDK requires Node 24+. Pass -NodePath with a Node 24 executable.' }
$env:PATH = (Split-Path -Parent $node) + ';' + (Join-Path $taskRoot '.tools/zig-x86_64-windows-0.16.0') + ';' + $env:PATH
$env:NATIVE_SDK_PATH = $sdk
$env:NATIVE_SDK_SCRIPTC_CACHE = Join-Path $taskRoot '.tools/scriptc'
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $taskRoot '.tools/zig-cache'
function Check-Exit([string]$message) { if ($LASTEXITCODE -ne 0) { throw $message } }
function Download-Verified([string]$url, [string]$path, [string]$sha256) {
    if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $sha256) { throw "Checksum mismatch: $path" }
}
Push-Location -LiteralPath $taskRoot
try {
    if (!$SkipBootstrap) {
        New-Item -ItemType Directory -Path '.tools' -Force | Out-Null
        $npmCommand = (Get-Command npm.cmd -ErrorAction Stop).Source
        $npmCli = Join-Path (Split-Path -Parent $npmCommand) 'node_modules/npm/bin/npm-cli.js'
        & $node $npmCli install --prefix '.tools/native-sdk' --ignore-scripts --no-audit --no-fund --registry https://registry.npmjs.org @native-sdk/cli@0.11.0
        Check-Exit 'Native CLI restore failed.'
        if (!(Test-Path -LiteralPath $sdk)) {
            & git -c core.autocrlf=false clone --depth 1 --branch v0.11.0 https://github.com/vercel-labs/native.git $sdk
            Check-Exit 'Native SDK source download failed.'
        }
        if ((& git -C $sdk rev-parse HEAD) -ne '16f29572307c2cc133907537c4dfd98046d4e0f4') { throw 'Unexpected SDK source revision.' }
        Download-Verified 'https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip' '.tools/zig-0.16.0.zip' '68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e'
        if (!(Test-Path -LiteralPath '.tools/zig-x86_64-windows-0.16.0/zig.exe')) { Expand-Archive -LiteralPath '.tools/zig-0.16.0.zip' -DestinationPath '.tools' }
        $patch = Join-Path $PSScriptRoot 'native-sdk-windows-path.patch'
        & git -C $sdk apply --reverse --check $patch 2>$null
        if ($LASTEXITCODE -ne 0) { & git -C $sdk apply --check $patch; Check-Exit 'SDK patch conflict.'; & git -C $sdk apply $patch; Check-Exit 'SDK patch failed.' }
        Push-Location -LiteralPath (Join-Path $sdk 'packages/core')
        try {
            & $node $npmCli ci --ignore-scripts --include=dev --no-audit --no-fund --registry https://registry.npmjs.org
            Check-Exit 'Native compiler frontend restore failed.'
            & $node --use-env-proxy scripts/install_scriptc.mjs
            Check-Exit 'Pinned scriptc toolchain installation failed.'
        } finally { Pop-Location }
        Download-Verified 'https://raw.githubusercontent.com/google/fonts/main/ofl/notosanssc/NotoSansSC%5Bwght%5D.ttf' '.tools/NotoSansSC.ttf' 'A3041811A78C361B1DE50F953C805E0244951C21C5BD412F7232EF0D899AF0DA'
        & $PythonPath -m pip install --target '.tools/fonttools' fonttools==4.60.1 --disable-pip-version-check --index-url https://pypi.org/simple
        Check-Exit 'FontTools restore failed.'
        $savedPythonPath = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = Join-Path $taskRoot '.tools/fonttools'
            & $PythonPath (Join-Path $PSScriptRoot 'Prepare-NativeFont.py') '.tools/NotoSansSC.ttf' (Join-Path $prototype 'assets/NotoSansSC-Regular.ttf')
            Check-Exit 'CJK font preparation failed.'
        } finally { $env:PYTHONPATH = $savedPythonPath }
    }
    $native = Join-Path $taskRoot '.tools/native-sdk/node_modules/@native-sdk/cli/bin/native.js'
    Push-Location -LiteralPath $prototype
    try {
        & $node $native build --yes -Dautomation=true
        Check-Exit 'Native executable build failed.'
        & $node $native check
        Check-Exit 'Native markup validation failed.'
        & $node --import ./tests/register-sdk.mjs --test tests/core.test.ts
        Check-Exit 'Native prototype behavior tests failed.'
    } finally { Pop-Location }
    & (Join-Path $PSScriptRoot 'Bootstrap.ps1')
    $dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    $output = Join-Path $taskRoot '.artifacts/native-validation'
    & $dotnet publish src/AileArc.NativeValidation/AileArc.NativeValidation.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $output 'backend') -v minimal
    Check-Exit 'Native validation backend publish failed.'
    & $dotnet publish src/AileArc.Worker/AileArc.Worker.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $output 'backend/Worker') -v minimal
    Check-Exit 'Worker publish failed.'
    Copy-Item -LiteralPath (Join-Path $prototype 'zig-out/bin/ailearc-native.exe') -Destination $output
    Copy-Item -LiteralPath (Join-Path $prototype 'assets') -Destination $output -Recurse -Force
    Copy-Item -LiteralPath 'LICENSE','THIRD_PARTY_NOTICES.md' -Destination $output
    Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE') -Destination (Join-Path $output 'NativeSDK-LICENSE.txt')
    $notices = Join-Path $output 'Notices'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Copy-Item -LiteralPath '.tools/dotnet/LICENSE.txt','.tools/dotnet/ThirdPartyNotices.txt' -Destination $notices
    # Preserve all license texts accompanying the linked compiler/runtime pack.
    Get-ChildItem -LiteralPath $env:NATIVE_SDK_SCRIPTC_CACHE -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING)' } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($env:NATIVE_SDK_SCRIPTC_CACHE, $_.FullName)
        $destination = Join-Path $notices ('scriptc/' + $relative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
    Write-Host "Native validation output: $output"
} finally { Pop-Location }
