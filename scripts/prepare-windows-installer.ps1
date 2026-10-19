param([string]$Configuration = 'Release', [string]$NsisArchive)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
$toolRoot = Join-Path $repository 'artifacts/build-tools'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
if (-not $NsisArchive) {
    $NsisArchive = Join-Path $toolRoot 'nsis-3.13.zip'
    if (-not (Test-Path -LiteralPath $NsisArchive) -or (Get-FileHash -Algorithm SHA256 -LiteralPath $NsisArchive).Hash -ne $expectedHash) {
        $download = Join-Path $toolRoot ('nsis-download-' + [guid]::NewGuid().ToString('N') + '.zip')
        try {
            # Official SourceForge mirror. Downloading is only a developer preparation step.
            Invoke-WebRequest -Uri 'https://sf-eu-introserv-3.dl.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip?viasf=1' -OutFile $download -TimeoutSec 60
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $download).Hash -ne $expectedHash) { throw 'NSIS archive SHA-256 mismatch.' }
            Move-Item -LiteralPath $download -Destination $NsisArchive -Force
        }
        finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
    }
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $NsisArchive).Hash -ne $expectedHash) { throw 'Expected the official NSIS 3.13 ZIP with the pinned SHA-256.' }
Expand-Archive -LiteralPath $NsisArchive -DestinationPath $toolRoot -Force
$compiler = Join-Path $toolRoot 'nsis-3.13/makensis.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'NSIS compiler missing after extraction.' }
& $compiler /VERSION
if ($LASTEXITCODE -ne 0) { throw 'NSIS compiler does not run.' }
& (Join-Path $PSScriptRoot 'prepare-windows-export.ps1') -Configuration $Configuration
Write-Output "Windows installer compiler prepared: $compiler"
