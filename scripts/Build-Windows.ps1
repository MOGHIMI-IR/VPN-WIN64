[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $assets = Join-Path $root 'src/MixMoghimi/Assets'
    $engineDir = Join-Path $assets 'engine'
    New-Item -ItemType Directory -Force -Path $engineDir | Out-Null

    $zip = Join-Path $env:TEMP 'mix-sing-box-win64.zip'
    $extract = Join-Path $env:TEMP 'mix-sing-box-extract'
    $coreUrl = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip'
    Invoke-WebRequest -Uri $coreUrl -OutFile $zip
    $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'
    if ($actual -ne $expected) { throw "sing-box SHA-256 mismatch: $actual" }

    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $exe = Get-ChildItem -Path $extract -Filter 'sing-box.exe' -Recurse | Select-Object -First 1
    if (-not $exe) { throw 'sing-box.exe is missing from the verified archive.' }
    Copy-Item $exe.FullName (Join-Path $engineDir 'sing-box.exe') -Force

    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/google/fonts/main/ofl/vazirmatn/Vazirmatn%5Bwght%5D.ttf' -OutFile (Join-Path $assets 'Vazirmatn.ttf')
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/google/fonts/main/ofl/vazirmatn/OFL.txt' -OutFile (Join-Path $assets 'OFL.txt')
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/SagerNet/sing-box/v1.14.4/LICENSE' -OutFile (Join-Path $assets 'sing-box-LICENSE.txt')

    if ((Get-Item (Join-Path $assets 'Vazirmatn.ttf')).Length -lt 10000) { throw 'Vazirmatn font download is invalid.' }

    dotnet restore 'tests/MixMoghimi.Tests/MixMoghimi.Tests.csproj'
    if (-not $SkipTests) {
        dotnet test 'tests/MixMoghimi.Tests/MixMoghimi.Tests.csproj' -c Release --no-restore
    }
    dotnet build 'src/MixMoghimi/MixMoghimi.csproj' -c Release --no-restore

    $output = Join-Path $root 'artifacts/MIX-MOGHIMI-win-x64'
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    dotnet publish 'src/MixMoghimi/MixMoghimi.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'engine') | Out-Null
    Copy-Item (Join-Path $engineDir 'sing-box.exe') (Join-Path $output 'engine/sing-box.exe') -Force
    Copy-Item (Join-Path $assets 'OFL.txt') (Join-Path $output 'Vazirmatn-OFL.txt') -Force
    Copy-Item (Join-Path $assets 'sing-box-LICENSE.txt') (Join-Path $output 'sing-box-LICENSE.txt') -Force

    Write-Host "Build completed: $output" -ForegroundColor Green
}
finally {
    Pop-Location
}
