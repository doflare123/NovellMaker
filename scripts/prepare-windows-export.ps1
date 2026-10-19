param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repository = Split-Path $PSScriptRoot -Parent
$template = Join-Path $repository 'artifacts/export-templates/win-x64'

# Run when preparing an editor distribution, never when exporting an author's game.
dotnet publish (Join-Path $repository 'src/NovellMaker.Runtime/NovellMaker.Runtime.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:NuGetLockFilePath=packages.win-x64.lock.json -p:RestoreLockedMode=true -o $template
if ($LASTEXITCODE -ne 0) { throw 'Windows self-contained runtime publication failed.' }
dotnet build (Join-Path $repository 'src/NovellMaker.Editor/NovellMaker.Editor.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Editor build failed.' }
Write-Output "Windows x64 export template prepared: $template"
