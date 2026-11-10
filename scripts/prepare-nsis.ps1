param([string]$NsisArchive)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
$toolRoot = Join-Path $repository 'artifacts/build-tools'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
if (-not $NsisArchive) {
    $NsisArchive = Join-Path $toolRoot 'nsis-3.13.zip'
    . (Join-Path $PSScriptRoot 'download-verified-archive.ps1')
    Get-VerifiedArchive -Destination $NsisArchive -Sha256 $expectedHash -Sources @(
        'https://sourceforge.net/projects/nsis/files/NSIS%203/3.13/nsis-3.13.zip/download',
        'https://netix.dl.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip',
        'https://pilotfiber.dl.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip',
        'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip'
    )
}
# Explicit offline archives use the same pin and never silently fall back to the network.
$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $NsisArchive).Hash
if ($actualHash -ne $expectedHash) {
    throw "Expected the official NSIS 3.13 ZIP. SHA-256: expected $expectedHash, received $actualHash."
}
Expand-Archive -LiteralPath $NsisArchive -DestinationPath $toolRoot -Force
$compiler = Join-Path $toolRoot 'nsis-3.13/makensis.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'NSIS compiler missing after extraction.' }
$version = & $compiler /VERSION
if ($LASTEXITCODE -ne 0 -or "$version".Trim() -ne 'v3.13') { throw 'Unexpected NSIS compiler version.' }
Write-Output "Windows installer compiler prepared: $compiler ($version)"
