param([string]$Configuration = 'Release', [string]$NsisArchive)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'prepare-nsis.ps1') -NsisArchive $NsisArchive
& (Join-Path $PSScriptRoot 'prepare-windows-export.ps1') -Configuration $Configuration
