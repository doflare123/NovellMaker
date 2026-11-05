param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported host OS.' }
if ($architecture -notin @('x64', 'arm64')) { throw 'Native portability checks require x64 or arm64.' }
$rid = "$platform-$architecture"
$repository = Split-Path $PSScriptRoot -Parent
$run = Join-Path $repository ('artifacts/native-portability/' + $rid + '/' + [guid]::NewGuid().ToString('N'))
$runtime = Join-Path $run 'runtime'
$results = Join-Path $run 'TestResults'
$names = @('AVALONIA_TELEMETRY_OPTOUT', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'NOVELLMAKER_NATIVE_SMOKE',
    'NOVELLMAKER_NATIVE_RUNTIME_DIR', 'NOVELLMAKER_NATIVE_EVIDENCE_DIR', 'NOVELLMAKER_DELIVERY_EDITOR_DIR')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
New-Item -ItemType Directory -Path $run -Force | Out-Null
Push-Location $repository
try {
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    # A publication probe, not an export template or an installer qualification.
    # Isolate RID restore assets/lock from the ordinary locked solution restore.
    dotnet publish src/NovellMaker.Runtime/NovellMaker.Runtime.csproj -c $Configuration -r $rid --self-contained true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None `
        -p:NuGetLockFilePath="obj/native-portability/$rid/packages.lock.json" -p:RestoreLockedMode=false `
        -p:MSBuildProjectExtensionsPath="obj/native-portability/$rid/" -o $runtime
    if ($LASTEXITCODE -ne 0) { throw 'Native runtime publication failed.' }
    dotnet restore NovellMaker.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
    foreach ($suite in @('Core', 'Runtime')) {
        dotnet build "tests/NovellMaker.$suite.Tests/NovellMaker.$suite.Tests.csproj" -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "$suite build failed." }
    }
    $env:NOVELLMAKER_NATIVE_SMOKE = '1'
    $env:NOVELLMAKER_NATIVE_RUNTIME_DIR = $runtime
    $env:NOVELLMAKER_NATIVE_EVIDENCE_DIR = Join-Path $run 'evidence'
    Remove-Item Env:NOVELLMAKER_DELIVERY_EDITOR_DIR -ErrorAction SilentlyContinue
    foreach ($suite in @('Core', 'Runtime')) {
        dotnet test "tests/NovellMaker.$suite.Tests/NovellMaker.$suite.Tests.csproj" -c $Configuration --no-build `
            --filter 'TestCategory=NativePortability' --logger "trx;LogFileName=$suite.trx" --results-directory $results `
            --blame-hang-timeout 90s --blame-hang-dump-type none
        if ($LASTEXITCODE -ne 0) { throw "$suite native portability checks failed." }
    }
    & (Join-Path $PSScriptRoot 'verify-release-results.ps1') -ResultsDirectory $results -Profile NativePortability
    [ordered]@{
        rid = $rid; checkedAtUtc = [DateTime]::UtcNow.ToString('O')
        stages = @('self-contained-publish', 'native-render-audio', 'screen-lua-pause-resume', 'game-lua-budget-recovery', 'worker-cancellation-reaps-child')
        exportQualified = $false
        workerMemoryLimit = if ($IsWindows) { 'windows-job-256MiB-and-managed-heap-64MiB' } else { 'managed-heap-64MiB-only' }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'portability.json') -Encoding utf8
    Write-Output "Native runtime portability checks passed: $run"
}
finally {
    Pop-Location
    foreach ($name in $names) {
        if ($null -eq $previous[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    }
}
