param([Parameter(Mandatory)][string]$CandidateDirectory,
      [Parameter(Mandatory)][string]$Destination,
      [Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Building the editor installer requires Windows.' }
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid version.' }
$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$repository = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $repository 'artifacts/build-tools/nsis-3.13/makensis.exe'
$candidate = [IO.Path]::GetFullPath($CandidateDirectory)
$Destination = [IO.Path]::GetFullPath($Destination)
foreach ($file in @('NovellMaker.Editor.exe', 'coreclr.dll', 'Runtime/NovellMaker.Runtime.exe',
    'ExportTemplates/win-x64/runtime-template.json', 'BuildTools/NSIS/makensis.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $candidate $file) -PathType Leaf)) { throw "Installer payload missing $file" }
}
$work = Join-Path $repository ('artifacts/editor-installers/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
function Quote-Nsis([string]$Value) {
    if ($Value -match '[\r\n]') { throw 'Newline in installer path.' }
    '"' + $Value.Replace('$', '${U+24}').Replace('"', '$\"') + '"'
}
$install = [Text.StringBuilder]::new()
$remove = [Text.StringBuilder]::new()
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $candidate -Recurse -File | Sort-Object FullName) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Installer payload contains a link.' }
    $relative = [IO.Path]::GetRelativePath($candidate, $file.FullName).Replace('/', '\')
    if ($relative -match '(^|\\)\.\.($|\\)') { throw 'Installer payload escaped candidate.' }
    $directory = [IO.Path]::GetDirectoryName($relative)
    $targetDirectory = if ($directory) { '$INSTDIR\' + $directory.Replace('$', '${U+24}') } else { '$INSTDIR' }
    [void]$install.AppendLine('SetOutPath "' + $targetDirectory + '"')
    [void]$install.AppendLine('IfErrors failed')
    [void]$install.AppendLine('File ' + (Quote-Nsis ('/oname=' + $file.Name)) + ' ' + (Quote-Nsis $file.FullName))
    [void]$install.AppendLine('IfErrors failed')
    [void]$remove.AppendLine('Delete "$INSTDIR\' + $relative.Replace('$', '${U+24}') + '"')
    while ($directory) { [void]$directories.Add($directory); $directory = [IO.Path]::GetDirectoryName($directory) }
}
$removeDirectories = ($directories | Sort-Object Length -Descending | ForEach-Object { 'RMDir "$INSTDIR\' + $_.Replace('$', '${U+24}') + '"' }) -join "`n"
$template = @'
Unicode true
ManifestSupportedOS Win10
RequestExecutionLevel user
SetCompressor /SOLID zlib
CRCCheck force
AllowSkipFiles off
Name "NovellMaker 0.1 beta"
OutFile "NovellMaker-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\NovellMaker"
VIProductVersion "@NUMERIC@"
VIAddVersionKey /LANG=1049 "ProductName" "NovellMaker"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка редактора NovellMaker"
VIAddVersionKey /LANG=1049 "FileVersion" "@NUMERIC@"
VIAddVersionKey /LANG=1049 "ProductVersion" "@VERSION@"
VIAddVersionKey /LANG=1049 "LegalCopyright" ""
!include "MUI2.nsh"
!include "x64.nsh"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\NovellMaker.Editor.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить NovellMaker"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"
!define APPKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\NovellMaker.Editor"

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "NovellMaker requires Windows x64." /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  ReadRegStr $0 HKCU "${APPKEY}" "InstallLocation"
  StrCmp $0 "" ready
    MessageBox MB_OK|MB_ICONSTOP "Сначала удалите установленную версию NovellMaker через параметры Windows. Проекты и пользовательские данные сохранятся." /SD IDOK
    SetErrorLevel 2
    Quit
  ready:
FunctionEnd

Function CheckEmptyDirectory
  FindFirst $0 $1 "$INSTDIR\*"
  loop:
    StrCmp $1 "" empty
    StrCmp $1 "." next
    StrCmp $1 ".." next
    FindClose $0
    SetErrors
    Return
  next:
    FindNext $0 $1
    Goto loop
  empty:
    FindClose $0
    ClearErrors
FunctionEnd

Function .onVerifyInstDir
  Call CheckEmptyDirectory
  IfErrors 0 valid
  Abort
  valid:
FunctionEnd

Section "NovellMaker"
  Call CheckEmptyDirectory
  IfErrors 0 install
    MessageBox MB_OK|MB_ICONSTOP "Выберите новую пустую папку для NovellMaker." /SD IDOK
    SetErrorLevel 2
    Quit
  install:
  SetOutPath "$INSTDIR"
  ClearErrors
  WriteINIStr "$INSTDIR\novellmaker-install.ini" "Install" "Application" "NovellMaker.Editor"
  IfErrors failed
  @INSTALL@
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors failed
  CreateDirectory "$SMPROGRAMS\NovellMaker"
  CreateShortcut "$SMPROGRAMS\NovellMaker\NovellMaker.lnk" "$INSTDIR\NovellMaker.Editor.exe"
  CreateShortcut "$SMPROGRAMS\NovellMaker\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APPKEY}" "DisplayName" "NovellMaker 0.1 beta"
  WriteRegStr HKCU "${APPKEY}" "DisplayVersion" "@VERSION@"
  WriteRegStr HKCU "${APPKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${APPKEY}" "DisplayIcon" "$INSTDIR\NovellMaker.Editor.exe"
  WriteRegStr HKCU "${APPKEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${APPKEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${APPKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${APPKEY}" "NoRepair" 1
  IfErrors failed
  Goto done
  failed:
    SetErrorLevel 3
    Abort "Не удалось завершить установку. Проверьте права записи и свободное место."
  done:
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadINIStr $0 "$INSTDIR\novellmaker-install.ini" "Install" "Application"
  StrCmp $0 "NovellMaker.Editor" 0 invalid
  ReadRegStr $0 HKCU "${APPKEY}" "InstallLocation"
  StrCmp $0 "$INSTDIR" valid
  invalid:
    SetErrorLevel 2
    Quit
  valid:
FunctionEnd

Function .onInstFailed
  ReadINIStr $0 "$INSTDIR\novellmaker-install.ini" "Install" "Application"
  StrCmp $0 "NovellMaker.Editor" 0 done
  @REMOVE@
  @DIRECTORIES@
  Delete "$INSTDIR\novellmaker-install.ini"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$SMPROGRAMS\NovellMaker\NovellMaker.lnk"
  Delete "$SMPROGRAMS\NovellMaker\Uninstall.lnk"
  RMDir "$SMPROGRAMS\NovellMaker"
  ReadRegStr $0 HKCU "${APPKEY}" "InstallLocation"
  StrCmp $0 "$INSTDIR" 0 keepRegistry
  DeleteRegKey HKCU "${APPKEY}"
  keepRegistry:
  SetOutPath "$TEMP"
  RMDir "$INSTDIR"
  done:
FunctionEnd

Section "Uninstall"
  @REMOVE@
  @DIRECTORIES@
  Delete "$INSTDIR\novellmaker-install.ini"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$SMPROGRAMS\NovellMaker\NovellMaker.lnk"
  Delete "$SMPROGRAMS\NovellMaker\Uninstall.lnk"
  RMDir "$SMPROGRAMS\NovellMaker"
  DeleteRegKey HKCU "${APPKEY}"
  SetOutPath "$TEMP"
  RMDir "$INSTDIR"
SectionEnd
'@
$script = $template.Replace('@VERSION@', $Version).Replace('@NUMERIC@', $numericVersion).
    Replace('@INSTALL@', $install.ToString()).Replace('@REMOVE@', $remove.ToString()).Replace('@DIRECTORIES@', $removeDirectories)
$scriptPath = Join-Path $work 'editor.nsi'
$script | Set-Content -LiteralPath $scriptPath -Encoding utf8BOM
Push-Location $work
try {
    & $compiler /V2 $scriptPath
    if ($LASTEXITCODE -ne 0) { throw 'Editor installer compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $work 'NovellMaker-Setup.exe') -Destination $Destination -Force
}
finally { Pop-Location }
Write-Output "Editor installer: $Destination"
