#requires -Version 7.0
param([string]$OutputDirectory = '', [switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRoot '.artifacts/shell' }
$toolRoot = Join-Path $taskRoot '.tools'
$version = '20260922'
$name = "llvm-mingw-$version-ucrt-x86_64"
$compiler = Join-Path $toolRoot "$name/bin/x86_64-w64-mingw32-clang++.exe"
if (!(Test-Path -LiteralPath $compiler)) {
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    $archive = Join-Path $toolRoot "$name.zip"
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest "https://github.com/mstorsjo/llvm-mingw/releases/download/$version/$name.zip" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'E3AD77D117A4BEA19A7A3B333341824D79A5A371004A10E25B8504E7B3047666') { throw 'LLVM-MinGW checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$header = @('// Generated from the official .resw resources; do not edit.')
foreach ($language in @(@('zh-CN','shellChinese'), @('en-US','shellEnglish'))) {
    [xml]$resources = Get-Content -LiteralPath (Join-Path $taskRoot "src/AileArc.Core/Strings/$($language[0])/Resources.resw") -Raw -Encoding utf8
    $values = @('L"AileArc"')
    foreach ($key in @('ShellOpen','SmartExtract','ExtractTo','CreateArchive')) {
        $value = @($resources.root.data | Where-Object name -EQ $key)[0].value
        $values += 'L"' + $value.Replace('\','\\').Replace('"','\"').Replace("`r",'\r').Replace("`n",'\n') + '"'
    }
    $header += 'static const wchar_t* ' + $language[1] + '[] = {' + ($values -join ', ') + '};'
}
$header | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ShellStrings.h') -Encoding utf8
& $compiler -std=c++20 -O2 -Wall -Wextra -Werror -municode -shared -static -DUNICODE -D_UNICODE -I $OutputDirectory (Join-Path $taskRoot 'src/AileArc.Shell/Shell.cpp') (Join-Path $taskRoot 'src/AileArc.Shell/Shell.def') -o (Join-Path $OutputDirectory 'AileArc.Shell.dll') -lole32 -lshell32 -lshlwapi -luuid
if ($LASTEXITCODE -ne 0) { throw 'Shell build failed.' }
if ($Test) {
    $testRoot = Join-Path $taskRoot ('.artifacts/shell-test-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'Shell') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'AileArc.Shell.dll') -Destination (Join-Path $testRoot 'Shell')
    $testExe = Join-Path $testRoot 'tests.exe'
    & $compiler -std=c++20 -O2 -Wall -Wextra -Werror -municode -static -DUNICODE -D_UNICODE -I $OutputDirectory (Join-Path $taskRoot 'tests/AileArc.Shell.Tests.cpp') -o $testExe -lole32 -lshell32 -lshlwapi -luuid
    if ($LASTEXITCODE -ne 0) { throw 'Shell test build failed.' }
    & $testExe $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Shell tests failed.' }
}
