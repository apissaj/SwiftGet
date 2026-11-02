<#
.SYNOPSIS
    Bangun SwiftGet dalam mode rilis.

.DESCRIPTION
    Menghasilkan dua varian di folder `publish`:

    - portable        : self-contained, satu folder, tanpa perlu .NET terpasang.
    - framework-dependent : satu exe kecil, memerlukan .NET Desktop Runtime 8.

.PARAMETER Target
    portable | framework-dependent | all  (default: all)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Target portable
#>
[CmdletBinding()]
param(
    [ValidateSet('portable', 'framework-dependent', 'all')]
    [string]$Target = 'all'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\SwiftGet.App\SwiftGet.App.csproj'
$publishRoot = Join-Path $root 'publish'

function Write-Step([string]$Text) {
    Write-Host "==> $Text" -ForegroundColor Cyan
}

Write-Step 'Menjalankan unit test'
dotnet test (Join-Path $root 'SwiftGet.sln') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Unit test gagal; publish dibatalkan.' }

if ($Target -in @('portable', 'all')) {
    Write-Step 'Publish portable (self-contained)'
    $out = Join-Path $publishRoot 'portable'
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=true `
        -o $out -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish portable gagal.' }

    $exe = Join-Path $out 'SwiftGet.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "    $exe ($size MB)" -ForegroundColor Green
}

if ($Target -in @('framework-dependent', 'all')) {
    Write-Step 'Publish framework-dependent (butuh .NET Desktop Runtime 8)'
    $out = Join-Path $publishRoot 'framework-dependent'
    dotnet publish $project -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -o $out -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish framework-dependent gagal.' }

    $exe = Join-Path $out 'SwiftGet.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1KB, 0)
    Write-Host "    $exe ($size KB)" -ForegroundColor Green
}

Write-Step 'Selesai'
