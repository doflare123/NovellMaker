param([string]$Configuration = 'Release', [string]$NsisArchive)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Release delivery qualification requires a Windows x64 host with desktop graphics and audio.'
}
$repository = Split-Path $PSScriptRoot -Parent
$run = Join-Path $repository ('artifacts/release/' + [guid]::NewGuid().ToString('N'))
$candidate = Join-Path $run 'NovellMaker'
$results = Join-Path $run 'TestResults'
$names = @('AVALONIA_TELEMETRY_OPTOUT', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'NOVELLMAKER_NATIVE_SMOKE',
    'NOVELLMAKER_EXPORT_SMOKE', 'NOVELLMAKER_INSTALLER_SMOKE', 'NOVELLMAKER_DELIVERY_EDITOR_DIR', 'NOVELLMAKER_DELIVERY_EVIDENCE_DIR',
    'NOVELLMAKER_NATIVE_RUNTIME_DIR', 'NOVELLMAKER_NATIVE_EVIDENCE_DIR')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
New-Item -ItemType Directory -Path $run | Out-Null
Push-Location $repository
try {
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Remove-Item Env:NOVELLMAKER_NATIVE_RUNTIME_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:NOVELLMAKER_NATIVE_EVIDENCE_DIR -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'prepare-windows-installer.ps1') -Configuration $Configuration -NsisArchive $NsisArchive
    dotnet restore NovellMaker.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
    dotnet build NovellMaker.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    dotnet publish src/NovellMaker.Editor/NovellMaker.Editor.csproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None `
        -p:NuGetLockFilePath=packages.win-x64.lock.json -p:RestoreLockedMode=true -o $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Editor candidate publication failed.' }
    # Preview must ship the same autonomous host as game export, not a framework-dependent build output.
    $preview = [IO.Path]::GetFullPath((Join-Path $candidate 'Runtime'))
    if ([IO.Path]::GetDirectoryName($preview) -ne [IO.Path]::GetFullPath($candidate)) { throw 'Preview path escaped candidate.' }
    if (Test-Path -LiteralPath $preview) { Remove-Item -LiteralPath $preview -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $candidate 'ExportTemplates/win-x64') -Destination $preview -Recurse
    & (Join-Path $PSScriptRoot 'copy-release-content.ps1') -Destination $candidate
    # Test bytes extracted from the deliverable ZIP, including its directory structure and native dependencies.
    $stagedArchive = Join-Path $run '.candidate.zip'
    Compress-Archive -LiteralPath $candidate -DestinationPath $stagedArchive
    Expand-Archive -LiteralPath $stagedArchive -DestinationPath (Join-Path $run 'unpacked')
    $candidate = Join-Path $run 'unpacked/NovellMaker'
    foreach ($file in @('NovellMaker.Editor.exe', 'coreclr.dll', 'Runtime/NovellMaker.Runtime.exe',
        'Runtime/coreclr.dll', 'ExportTemplates/win-x64/runtime-template.json', 'BuildTools/NSIS/makensis.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $candidate $file) -PathType Leaf)) { throw "Candidate missing $file" }
    }
    $env:NOVELLMAKER_NATIVE_SMOKE = '1'
    $env:NOVELLMAKER_EXPORT_SMOKE = '1'
    $env:NOVELLMAKER_INSTALLER_SMOKE = '1'
    $env:NOVELLMAKER_DELIVERY_EDITOR_DIR = $candidate
    $env:NOVELLMAKER_DELIVERY_EVIDENCE_DIR = Join-Path $run 'evidence'
    # Explicit test inventory plus TRX validation makes disabled, missing or inconclusive tests a hard failure.
    foreach ($suite in @('Runtime', 'Editor')) {
        dotnet test "tests/NovellMaker.$suite.Tests/NovellMaker.$suite.Tests.csproj" -c $Configuration --no-build `
            --filter 'TestCategory=Delivery' --logger "trx;LogFileName=$suite.trx" --results-directory $results `
            --blame-hang-timeout 120s --blame-hang-dump-type none
        if ($LASTEXITCODE -ne 0) { throw "$suite delivery checks failed." }
    }
    & (Join-Path $PSScriptRoot 'verify-release-results.ps1') -ResultsDirectory $results
    $commit = git -c "safe.directory=$repository" rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify qualified commit.' }
    $changes = git -c "safe.directory=$repository" status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify candidate worktree changes.' }
    $dirty = @($changes).Count -gt 0
    if ($env:GITHUB_ACTIONS -eq 'true' -and $dirty) { throw 'CI candidate worktree changed during qualification.' }
    $archive = Join-Path $run 'NovellMaker-win-x64.zip'
    Move-Item -LiteralPath $stagedArchive -Destination $archive
    $evidence = [ordered]@{
        commit = $commit; worktreeDirty = $dirty; rid = 'win-x64'; checkedAtUtc = [DateTime]::UtcNow.ToString('O')
        archive = [IO.Path]::GetFileName($archive); sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        stages = @('unpack-editor', 'editor-launch', 'create-project', 'preview', 'export', 'install', 'launch', 'save-load', 'uninstall-preserves-data', 'reinstall-load',
            'reading-controls-history', 'character-layered-flipbook-restore', 'voice-cursor-pause-resume-completion')
    }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'qualification.json') -Encoding utf8
    if ($env:GITHUB_OUTPUT) { "directory=$run" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    Write-Output "Qualified Windows x64 candidate: $run"
}
finally {
    Pop-Location
    foreach ($name in $names) {
        if ($null -eq $previous[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    }
}
