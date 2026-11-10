param([Parameter(Mandatory)][string]$Installer,
      [Parameter(Mandatory)][string]$CandidateDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Editor installer smoke requires Windows.' }
$repository = Split-Path $PSScriptRoot -Parent
$root = [IO.Path]::GetFullPath((Join-Path $repository ('artifacts/editor-installer-tests/' + [guid]::NewGuid().ToString('N'))))
$install = Join-Path $root 'Installed Editor'
$profileFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('NovellMaker/installer-smoke-' + [guid]::NewGuid().ToString('N') + '.txt')
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
$key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\NovellMaker.Editor'
$existing = $registry.OpenSubKey($key)
if ($null -ne $existing) { $existing.Dispose(); $registry.Dispose(); throw 'An editor is already installed; refusing to change its registration.' }
New-Item -ItemType Directory -Path $root -Force | Out-Null
function Run-Installer([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Installer process timed out.' }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
try {
    # A populated destination must be rejected even in silent mode.
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    $foreign = Join-Path $install 'keep.txt'
    Set-Content -LiteralPath $foreign -Value 'foreign file' -NoNewline
    if ((Run-Installer $Installer "/S /D=$install") -eq 0) { throw 'Installer accepted a nonempty directory.' }
    if ((Get-Content -LiteralPath $foreign -Raw) -ne 'foreign file') { throw 'Installer modified a foreign file.' }
    Remove-Item -LiteralPath $foreign
    if ((Run-Installer $Installer "/S /D=$install") -ne 0) { throw 'Editor installation failed.' }
    foreach ($file in Get-ChildItem -LiteralPath $CandidateDirectory -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($CandidateDirectory), $file.FullName)
        $installedFile = Join-Path $install $relative
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installedFile).Hash) {
            throw "Installed payload differs: $relative"
        }
    }
    $entry = $registry.OpenSubKey($key)
    try { if ($null -eq $entry -or $entry.GetValue('InstallLocation') -ne $install) { throw 'Missing editor registration.' } }
    finally { if ($null -ne $entry) { $entry.Dispose() } }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'NovellMaker/NovellMaker.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Missing Start menu shortcut.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'NovellMaker.Editor.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $root
    $start.Environment['DOTNET_ROOT'] = Join-Path $root 'no-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $root 'no-dotnet'
    $start.Environment['PATH'] = [Environment]::GetFolderPath('System')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.HasExited -and $timer.Elapsed.TotalSeconds -lt 20) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed editor did not create a window without SDK.' }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    # Preserve projects inside installation and data outside it, including nested directories.
    New-Item -ItemType Directory -Path (Join-Path $install 'MyProjects') -Force | Out-Null
    $projectFile = Join-Path $install 'MyProjects/keep.novell'
    Set-Content -LiteralPath $projectFile -Value 'user project' -NoNewline
    Set-Content -LiteralPath $profileFile -Value 'user profile' -NoNewline
    if ((Run-Installer (Join-Path $install 'Uninstall.exe') "/S _?=$install") -ne 0) { throw 'Editor uninstall failed.' }
    if ((Get-Content -LiteralPath $projectFile -Raw) -ne 'user project' -or
        (Get-Content -LiteralPath $profileFile -Raw) -ne 'user profile') { throw 'Uninstaller changed user data.' }
    foreach ($file in Get-ChildItem -LiteralPath $CandidateDirectory -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($CandidateDirectory), $file.FullName)
        if (Test-Path -LiteralPath (Join-Path $install $relative)) { throw "Uninstaller left an installed file: $relative" }
    }
    $entry = $registry.OpenSubKey($key)
    if ($null -ne $entry) { $entry.Dispose(); throw 'Uninstaller left registration.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Uninstaller left shortcut.' }
    Write-Output 'Editor installer checks passed: nonempty-directory rejection, exact payload, shortcut, SDK-free launch, uninstall preserves projects and profile.'
}
finally {
    # Clean only this uniquely named fixture; never a user installation or project.
    $entry = $registry.OpenSubKey($key)
    if ($null -ne $entry) {
        try { $owned = $entry.GetValue('InstallLocation') -eq $install }
        finally { $entry.Dispose() }
        if ($owned -and (Test-Path -LiteralPath (Join-Path $install 'Uninstall.exe'))) {
            [void](Run-Installer (Join-Path $install 'Uninstall.exe') "/S _?=$install")
        }
    }
    $registry.Dispose()
    if (Test-Path -LiteralPath $profileFile) { Remove-Item -LiteralPath $profileFile }
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/editor-installer-tests'))
    if ([IO.Path]::GetDirectoryName($root) -ne $expectedParent) { throw 'Fixture cleanup escaped workspace.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
