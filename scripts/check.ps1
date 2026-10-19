param([switch]$CaptureScreenshots, [switch]$NativeRuntime, [switch]$WindowsExport, [switch]$WindowsInstaller)

$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    if ($WindowsInstaller) {
        if (-not $IsWindows) { throw 'Windows installer smoke requires Windows x64.' }
        & (Join-Path $PSScriptRoot 'prepare-windows-installer.ps1')
        $env:NOVELLMAKER_INSTALLER_SMOKE = '1'
        $env:NOVELLMAKER_EXPORT_SMOKE = '1'
    }
    elseif ($WindowsExport) {
        if (-not $IsWindows) { throw 'Windows export smoke requires Windows x64.' }
        & (Join-Path $PSScriptRoot 'prepare-windows-export.ps1')
        $env:NOVELLMAKER_EXPORT_SMOKE = '1'
    }
    if ($CaptureScreenshots) {
        $env:NOVELLMAKER_CAPTURE_DIR = Join-Path (Get-Location) 'artifacts/screenshots'
    }
    if ($NativeRuntime) { $env:NOVELLMAKER_NATIVE_SMOKE = '1' }
    dotnet restore NovellMaker.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build NovellMaker.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test NovellMaker.slnx -c Release --no-build --logger trx --results-directory TestResults --blame-hang-timeout 60s --blame-hang-dump-type none
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
