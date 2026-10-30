param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repository = Split-Path $PSScriptRoot -Parent
$template = Join-Path $repository 'artifacts/export-templates/win-x64'
$templateParent = Split-Path $template -Parent
$staging = Join-Path $templateParent ('.win-x64-preparing-' + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $templateParent ('.win-x64-previous-' + [guid]::NewGuid().ToString('N'))

if (-not $IsWindows) { throw 'Preparing the Windows template requires Windows x64 to run the published manifest generator.' }
function Assert-TemplatePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($templateParent) -or
        ([IO.Path]::GetFileName($resolved) -ne 'win-x64' -and [IO.Path]::GetFileName($resolved) -notlike '.win-x64-*')) {
        throw 'Template operation escaped the export-templates directory.'
    }
}
foreach ($ownedPath in @($template, $staging, $backup)) { Assert-TemplatePath $ownedPath }

# Run when preparing an editor distribution, never when exporting an author's game.
try {
    dotnet publish (Join-Path $repository 'src/NovellMaker.Runtime/NovellMaker.Runtime.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:NuGetLockFilePath=packages.win-x64.lock.json -p:RestoreLockedMode=true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Windows self-contained runtime publication failed.' }
    # Debug symbols are developer-only. Unexpected payload files reject manifest generation.
    Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    & (Join-Path $staging 'NovellMaker.Runtime.exe') --prepare-export-template
    if ($LASTEXITCODE -ne 0) { throw 'Runtime template manifest generation failed.' }
    if (Test-Path -LiteralPath $template) { Move-Item -LiteralPath $template -Destination $backup }
    try { Move-Item -LiteralPath $staging -Destination $template }
    catch {
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $template }
        throw
    }
}
finally {
    foreach ($ownedPath in @($staging, $backup)) {
        Assert-TemplatePath $ownedPath
        if ($ownedPath -eq $backup -and -not (Test-Path -LiteralPath $template)) { continue }
        if (Test-Path -LiteralPath $ownedPath) { Remove-Item -LiteralPath $ownedPath -Recurse -Force }
    }
}
dotnet build (Join-Path $repository 'src/NovellMaker.Editor/NovellMaker.Editor.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Editor build failed.' }
Write-Output "Windows x64 export template prepared: $template"
