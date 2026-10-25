param([string]$Archive)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Pinned LuaLS verification tool currently targets Windows x64.' }
$version = '3.19.2'
$expectedHash = '4A3DC851B3B31E7B75A23F645D1BA4A2137579A55F5FA4EDBB17A3E2A6254044'
$toolRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/build-tools'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
if (-not $Archive) {
    $Archive = Join-Path $toolRoot "lua-language-server-$version-win32-x64.zip"
    if (-not (Test-Path -LiteralPath $Archive) -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $expectedHash) {
        $download = Join-Path $toolRoot ('luals-download-' + [guid]::NewGuid().ToString('N') + '.zip')
        try {
            Invoke-WebRequest -Uri "https://github.com/LuaLS/lua-language-server/releases/download/$version/lua-language-server-$version-win32-x64.zip" -OutFile $download -TimeoutSec 60
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) { throw 'LuaLS archive SHA-256 mismatch.' }
            Move-Item -LiteralPath $download -Destination $Archive -Force
        }
        finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
    }
}
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Expected the pinned official LuaLS 3.19.2 archive.' }
$destination = Join-Path $toolRoot "luals-$version"
Expand-Archive -LiteralPath $Archive -DestinationPath $destination -Force
$server = Join-Path $destination 'bin/lua-language-server.exe'
$versionOutput = & $server --version
if ($LASTEXITCODE -ne 0 -or "$versionOutput".Trim() -ne $version) { throw 'Unexpected LuaLS tool version.' }
Write-Output "LuaLS $version verification tool prepared: $server"
