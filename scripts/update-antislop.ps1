<#
.SYNOPSIS
    Unduh ulang berkas aturan antislop ke folder .antislop.

.DESCRIPTION
    Jalur file tunggal resmi (Route 11 dari panduan antislop). Aman dijalankan
    berulang kali: berkas yang ada akan ditimpa dengan versi terbaru dari
    branch main repositori anti-slop.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$destination = Join-Path $PSScriptRoot '..\.antislop' | Resolve-Path -ErrorAction SilentlyContinue
if (-not $destination) {
    $destination = Join-Path (Split-Path $PSScriptRoot -Parent) '.antislop'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
}

$base = 'https://raw.githubusercontent.com/miqdadbadjuber/anti-slop/main'

$sources = [ordered]@{
    'antislop.md'            = "$base/antislop.md"
    'antislop-ui.md'         = "$base/skills/antislop-ui/SKILL.md"
    'antislop-human.md'      = "$base/skills/antislop-human/SKILL.md"
    'antislop-copywriting.md' = "$base/skills/antislop-copywriting/SKILL.md"
    'antislop-code.md'       = "$base/skills/antislop-code/SKILL.md"
}

foreach ($name in $sources.Keys) {
    $target = Join-Path $destination $name
    try {
        Invoke-WebRequest -Uri $sources[$name] -OutFile $target -UseBasicParsing
        $kb = [math]::Round((Get-Item $target).Length / 1KB, 1)
        Write-Host "  $name ($kb KB)"
    }
    catch {
        Write-Warning "Gagal mengunduh $name : $($_.Exception.Message)"
    }
}

Write-Host "Aturan antislop diperbarui di $destination"
