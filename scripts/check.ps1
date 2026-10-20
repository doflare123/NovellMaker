param([switch]$CaptureScreenshots, [switch]$NativeRuntime, [switch]$WindowsExport, [switch]$WindowsInstaller,
      [switch]$Coverage, [switch]$MeasureLua)

$ErrorActionPreference = 'Stop'
$environmentNames = @('AVALONIA_TELEMETRY_OPTOUT', 'DOTNET_CLI_TELEMETRY_OPTOUT',
    'NOVELLMAKER_NATIVE_SMOKE', 'NOVELLMAKER_EXPORT_SMOKE', 'NOVELLMAKER_INSTALLER_SMOKE',
    'NOVELLMAKER_CAPTURE_DIR', 'NOVELLMAKER_LUA_BENCHMARK_DIR')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
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
    # The optional benchmark runs separately, after all functional tests and without coverage.
    if ($MeasureLua) { [Environment]::SetEnvironmentVariable('NOVELLMAKER_LUA_BENCHMARK_DIR', $null) }
    dotnet restore NovellMaker.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build NovellMaker.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $testArguments = @('test', 'NovellMaker.slnx', '-c', 'Release', '--no-build', '--logger', 'trx',
        '--results-directory', 'TestResults', '--blame-hang-timeout', '60s', '--blame-hang-dump-type', 'none')
    if ($Coverage) {
        # Separate runs so old reports cannot be mistaken for this run's coverage.
        $coverageDirectory = Join-Path 'TestResults' ('coverage-' + [guid]::NewGuid().ToString('N'))
        $testArguments = @('test', 'NovellMaker.slnx', '-c', 'Release', '--no-build', '--logger', 'trx',
            '--results-directory', $coverageDirectory, '--settings', 'tests/coverage.runsettings',
            '--collect', 'Code Coverage', '--blame-hang-timeout', '60s', '--blame-hang-dump-type', 'none')
    }
    dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Coverage) {
        & (Join-Path $PSScriptRoot 'summarize-coverage.ps1') -ResultsDirectory $coverageDirectory
    }
    if ($MeasureLua) {
        $env:NOVELLMAKER_LUA_BENCHMARK_DIR = Join-Path (Get-Location) 'artifacts/lua-benchmark'
        dotnet test tests/NovellMaker.Core.Tests -c Release --no-build --filter 'TestCategory=LuaMeasurement' --logger trx --results-directory TestResults/lua-measurement
        if ($LASTEXITCODE -ne 0) { throw 'Lua measurement failed.' }
    }
}
finally {
    Pop-Location
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
}
