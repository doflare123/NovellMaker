param([switch]$CaptureScreenshots)

$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    if ($CaptureScreenshots) {
        $env:NOVELLMAKER_CAPTURE_DIR = Join-Path (Get-Location) 'artifacts/screenshots'
    }
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
