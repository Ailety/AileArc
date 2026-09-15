#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $taskRoot '.tools'
$dotnetDir = Join-Path $toolRoot 'dotnet'
$dotnetExe = Join-Path $dotnetDir 'dotnet.exe'
$sdkVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'global.json') -Raw -Encoding utf8 | ConvertFrom-Json).sdk.version
if (-not (Test-Path -LiteralPath (Join-Path $dotnetDir "sdk/$sdkVersion"))) {
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $sdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -EQ $sdkVersion)[0]
    if (-not $sdk) { throw "SDK $sdkVersion not found in official metadata." }
    $package = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' })[0]
    $archive = Join-Path $toolRoot "dotnet-sdk-$sdkVersion.zip"
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading .NET SDK $sdkVersion (workspace-local)..."
        Invoke-WebRequest $package.url -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $package.hash) {
        throw 'SDK checksum mismatch. Remove the downloaded archive and retry.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $dotnetDir -Force
}
& $dotnetExe --version
if ($LASTEXITCODE -ne 0) { throw 'SDK validation failed.' }

$engineDir = Join-Path $taskRoot 'third_party/7zip/bin'
if (-not (Test-Path -LiteralPath (Join-Path $engineDir '7z.dll'))) {
    $downloadDir = Join-Path $toolRoot '7zip'
    New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
    $downloads = @(
        @{ Name = '7zr.exe'; Hash = 'AD4C82FADCBDF93C03B4FC440F300509C7D60C5C2F4D183E35D9D70D6957037D' },
        @{ Name = '7z2603-x64.exe'; Hash = '0859C524B8A63551848F0C246ABDDCB1D0B7B656B0FBFE879F8D85E61A9E6EDD' }
    )
    foreach ($download in $downloads) {
        $target = Join-Path $downloadDir $download.Name
        if (-not (Test-Path -LiteralPath $target)) {
            Invoke-WebRequest "https://github.com/ip7z/7zip/releases/download/26.03/$($download.Name)" -OutFile $target
        }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $download.Hash) { throw "7-Zip checksum mismatch: $($download.Name)" }
    }
    & (Join-Path $downloadDir '7zr.exe') x (Join-Path $downloadDir '7z2603-x64.exe') "-o$engineDir" -y
    if ($LASTEXITCODE -ne 0) { throw '7-Zip extraction failed.' }
}
