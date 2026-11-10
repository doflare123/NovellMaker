$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
. (Join-Path $repository 'scripts/download-verified-archive.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $repository ('TestResults/archive-download-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$fixture = Join-Path $root 'fixture.zip'
[IO.File]::WriteAllBytes($fixture, [Text.Encoding]::UTF8.GetBytes('pinned archive payload'))
$hash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
$script:requests = [Collections.Generic.List[string]]::new()
function Invoke-WebRequest {
    param([uri]$Uri, [string]$OutFile, [switch]$PassThru, [int]$TimeoutSec,
          [string]$UserAgent, [int]$MaximumRedirection)
    $script:requests.Add($Uri.AbsolutePath)
    if (-not $PassThru -or $TimeoutSec -gt 30 -or $UserAgent -ne 'Wget/1.21.4' -or $MaximumRedirection -ne 10) {
        throw 'Unexpected download options.'
    }
    switch ($Uri.AbsolutePath) {
        '/network' { throw 'Simulated transport failure.' }
        '/html' { [IO.File]::WriteAllText($OutFile, '<html>Not an archive</html>') }
        '/corrupt' { [IO.File]::WriteAllText($OutFile, 'corrupt ZIP') }
        '/valid' { Copy-Item -LiteralPath $fixture -Destination $OutFile }
        default { throw 'Unexpected test source.' }
    }
    [pscustomobject]@{ Headers = @{ 'Content-Type' = $(if ($Uri.AbsolutePath -eq '/html') { 'text/html' } else { 'application/octet-stream' }) } }
}
function Check([string]$Name, [string[]]$Sources, [string]$Existing, [bool]$Pass, [int]$Calls) {
    $script:requests.Clear()
    $destination = Join-Path $root ($Name + '.zip')
    if ($Existing -eq 'valid') { Copy-Item -LiteralPath $fixture -Destination $destination }
    elseif ($Existing) { [IO.File]::WriteAllText($destination, $Existing) }
    $accepted = $true
    $detail = ''
    try {
        Get-VerifiedArchive -Destination $destination -Sha256 $hash -Sources @($Sources | ForEach-Object { 'https://mirror.example/' + $_ }) -WarningAction SilentlyContinue
    }
    catch { $accepted = $false; $detail = $_.Exception.Message }
    if ($accepted -ne $Pass -or $script:requests.Count -ne $Calls) { throw "Archive regression: $Name ($detail)" }
    if ($Pass) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Unverified content accepted: $Name" }
    }
    elseif ($Existing) {
        if ([IO.File]::ReadAllText($destination) -ne $Existing) { throw "Failed download changed original cache: $Name" }
    }
    elseif (Test-Path -LiteralPath $destination) { throw "Failed download published a file: $Name" }
    if (@(Get-ChildItem -LiteralPath $root -Filter 'archive-download-*.tmp').Count -ne 0) { throw "Temporary download left behind: $Name" }
    if (-not $Pass -and ($detail -notmatch 'SHA-256 mismatch' -or $detail -notmatch 'text/html')) {
        throw "Missing integrity diagnostics: $Name"
    }
}
try {
    Check 'valid-cache-no-network' @('network') 'valid' $true 0
    Check 'html-200-falls-back' @('html', 'valid') '' $true 2
    Check 'hash-and-transport-failures-fall-back' @('corrupt', 'network', 'valid') '' $true 3
    Check 'invalid-cache-replaced-after-verification' @('html', 'valid') 'stale archive' $true 2
    Check 'all-sources-fail-preserves-cache' @('html', 'corrupt', 'network') 'stale archive' $false 3
    Check 'all-sources-fail-publishes-nothing' @('html', 'corrupt') '' $false 2
    $script:requests.Clear()
    $rejected = $false
    try { & (Join-Path $repository 'scripts/prepare-nsis.ps1') -NsisArchive $fixture | Out-Null }
    catch { $rejected = $_.Exception.Message -match 'Expected the official NSIS 3.13 ZIP' }
    if (-not $rejected -or $script:requests.Count -ne 0) { throw 'Invalid explicit archive did not fail offline.' }
    Write-Output 'Archive download regression cases passed (7 cases, no network).'
}
finally {
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repository 'TestResults'))
    if ([IO.Path]::GetDirectoryName($root) -ne $expectedParent) { throw 'Fixture cleanup escaped TestResults.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
