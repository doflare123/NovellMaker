param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { throw 'Publish directory does not exist.' }
foreach ($name in @('README.md', 'docs', 'Licenses')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $Destination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'examples') | Out-Null
foreach ($name in @('author-interface', 'character-animation')) {
    Copy-Item -LiteralPath (Join-Path $repository "examples/$name") -Destination (Join-Path $Destination 'examples') -Recurse -Force
}
