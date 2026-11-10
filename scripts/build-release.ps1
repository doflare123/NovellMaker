param([ValidateSet('Windows', 'Linux')][string]$Platform = 'Windows', [string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
[xml]$properties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid release version.' }
$output = Join-Path $repository "artifacts/releases/$version"
$run = Join-Path $repository ('artifacts/release-builds/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null
$telemetry = [Environment]::GetEnvironmentVariable('AVALONIA_TELEMETRY_OPTOUT')
Push-Location $repository
try {
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    if ($Platform -eq 'Windows') {
        if (-not $IsWindows) { throw 'Windows delivery qualification requires Windows x64.' }
        # check-release produces an archive only after every required Delivery case passes.
        & (Join-Path $PSScriptRoot 'check-release.ps1') -Configuration $Configuration | Tee-Object -Variable qualificationOutput
        $line = @($qualificationOutput | Where-Object { "$_" -like 'Qualified Windows x64 candidate: *' })
        if ($line.Count -ne 1) { throw 'Missing qualified Windows candidate.' }
        $qualified = ([string]$line[0]).Substring('Qualified Windows x64 candidate: '.Length)
        $archive = Join-Path $output "NovellMaker-$version-win-x64-portable.zip"
        Copy-Item -LiteralPath (Join-Path $qualified 'NovellMaker-win-x64.zip') -Destination $archive -Force
        $evidence = Get-Content -LiteralPath (Join-Path $qualified 'qualification.json') -Raw | ConvertFrom-Json
        $evidence.archive = [IO.Path]::GetFileName($archive)
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $evidence.sha256) { throw 'Copied archive hash mismatch.' }
        $payload = Join-Path $qualified 'unpacked/NovellMaker'
        $setup = Join-Path $run "NovellMaker-$version-win-x64-Setup.exe"
        & (Join-Path $PSScriptRoot 'build-editor-installer.ps1') -CandidateDirectory $payload -Destination $setup -Version $version
        & (Join-Path $PSScriptRoot 'check-editor-installer.ps1') -Installer $setup -CandidateDirectory $payload
        Copy-Item -LiteralPath $setup -Destination $output -Force
        $evidence | Add-Member -NotePropertyName installer -NotePropertyValue ([IO.Path]::GetFileName($setup))
        $evidence | Add-Member -NotePropertyName installerSha256 -NotePropertyValue ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)
        $evidence.stages += @('editor-installer-exact-payload', 'installed-editor-launch', 'editor-uninstall-preserves-projects-profile')
        $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'qualification-win-x64.json') -Encoding utf8
        # Expose the unpacked portable application as well as its GitHub download archive.
        $portable = [IO.Path]::GetFullPath((Join-Path $output 'NovellMaker'))
        if ([IO.Path]::GetDirectoryName($portable) -ne [IO.Path]::GetFullPath($output)) { throw 'Portable path escaped release directory.' }
        if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Recurse -Force }
        Copy-Item -LiteralPath $payload -Destination $portable -Recurse
        $oldArchive = Join-Path $output "NovellMaker-$version-win-x64.zip"
        if (Test-Path -LiteralPath $oldArchive) { Remove-Item -LiteralPath $oldArchive }
    }
    else {
        $candidate = Join-Path $run 'NovellMaker'
        $runtime = Join-Path $run 'runtime'
        foreach ($item in @(@('Runtime', $runtime), @('Editor', $candidate))) {
            dotnet publish "src/NovellMaker.$($item[0])/NovellMaker.$($item[0]).csproj" -c $Configuration -r linux-x64 --self-contained true `
                -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None `
                -p:NuGetLockFilePath=packages.linux-x64.lock.json -p:RestoreLockedMode=true -o $item[1]
            if ($LASTEXITCODE -ne 0) { throw "Linux $($item[0]) publication failed." }
        }
        $preview = [IO.Path]::GetFullPath((Join-Path $candidate 'Runtime'))
        if ([IO.Path]::GetDirectoryName($preview) -ne [IO.Path]::GetFullPath($candidate)) { throw 'Preview path escaped candidate.' }
        if (Test-Path -LiteralPath $preview) { Remove-Item -LiteralPath $preview -Recurse -Force }
        Copy-Item -LiteralPath $runtime -Destination $preview -Recurse
        & (Join-Path $PSScriptRoot 'copy-release-content.ps1') -Destination $candidate
        foreach ($file in @('NovellMaker.Editor', 'libcoreclr.so', 'libSkiaSharp.so', 'Runtime/NovellMaker.Runtime', 'Runtime/libcoreclr.so', 'Runtime/libSkiaSharp.so')) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidate $file) -PathType Leaf)) { throw "Linux candidate missing $file" }
        }
        # Write Unix modes explicitly: Windows ZIP/tar defaults lose the executable bits.
        $archive = Join-Path $output "NovellMaker-$version-linux-x64-experimental.tar.gz"
        $stream = [IO.File]::Create($archive)
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal)
        $writer = [System.Formats.Tar.TarWriter]::new($gzip, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $candidate -Recurse -File | Sort-Object FullName) {
                $relative = [IO.Path]::GetRelativePath($candidate, $file.FullName).Replace('\', '/')
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "NovellMaker/$relative")
                $entry.Mode = [IO.UnixFileMode]$(if ($relative -in @('NovellMaker.Editor', 'Runtime/NovellMaker.Runtime')) { 493 } else { 420 })
                $content = [IO.File]::OpenRead($file.FullName)
                try { $entry.DataStream = $content; $writer.WriteEntry($entry) }
                finally { $content.Dispose() }
            }
        }
        finally { $writer.Dispose(); $gzip.Dispose(); $stream.Dispose() }
        [ordered]@{
            version = $version; rid = 'linux-x64'; experimental = $true; nativeQualified = $false
            exportQualified = $false; builtOn = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            archive = [IO.Path]::GetFileName($archive); sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-linux-x64.json') -Encoding utf8
    }
    Copy-Item -LiteralPath (Join-Path $repository 'docs/RELEASE-0.1-beta.md') -Destination $output -Force
    $checksums = foreach ($file in Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Name -match '\.(exe|zip|tar\.gz)$' } | Sort-Object Name) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name
    }
    $checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding ascii
    $assets = Join-Path $repository "artifacts/github-release/$version"
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    $obsoleteAsset = Join-Path $assets "NovellMaker-$version-win-x64.zip"
    if (Test-Path -LiteralPath $obsoleteAsset) { Remove-Item -LiteralPath $obsoleteAsset }
    Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Name -match '\.(exe|zip|tar\.gz)$' -or $_.Name -eq 'SHA256SUMS' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $assets -Force }
    # Release body uses repository URLs: relative documentation links don't resolve in GitHub Release pages.
    $notes = Get-Content -LiteralPath (Join-Path $repository 'docs/RELEASE-0.1-beta.md') -Raw
    $notes = $notes.Replace('](lua-reference/', "](https://github.com/doflare123/NovellMaker/blob/v$version/docs/lua-reference/")
    $notes | Set-Content -LiteralPath (Join-Path $assets 'RELEASE-NOTES.md') -Encoding utf8
    if ($env:GITHUB_OUTPUT) { "release_directory=$output" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    Write-Output "Release files: $output"
    Write-Output "GitHub release assets: $assets"
}
finally {
    Pop-Location
    [Environment]::SetEnvironmentVariable('AVALONIA_TELEMETRY_OPTOUT', $telemetry)
}
