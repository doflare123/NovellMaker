using System.Text;

namespace NovellMaker.Editor.Services.Export;

internal static class WindowsInstallerScript
{
    // NSIS has both runtime variables and a compile-time preprocessor. Escape every dollar and quote.
    internal static string Quote(string text, bool runtimeVariables = true)
    {
        if (text.Length > 240 || text.Any(c => char.IsControl(c)))
            throw new ArgumentException("Имя и пути установщика не должны содержать управляющие символы и превышать 240 символов.");
        var result = new StringBuilder("\"");
        foreach (var character in text)
            result.Append(character switch
            {
                '$' => runtimeVariables ? "${U+24}${U+24}" : "${U+24}", '{' => "${U+7B}", '}' => "${U+7D}", '%' => "${U+25}",
                '"' => "$\\\"", _ => character.ToString()
            });
        return result.Append('"').ToString();
    }

    internal static string Create(Guid projectId, string title, WindowsInstallerOptions options, IReadOnlyList<string> files)
    {
        var version = options.ValidateVersion();
        var id = projectId.ToString("N");
        var key = $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NovellMaker-{id}";
        var install = new StringBuilder(); var remove = new StringBuilder(); var removeDirectories = new StringBuilder();
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (Path.IsPathFullyQualified(file) || file.Split('\\', '/').Any(s => s is "." or "..") || file.IndexOfAny(['*', '?', '\r', '\n']) >= 0)
                throw new ArgumentException("Некорректный путь файла установщика.");
            var windowsPath = file.Replace('/', '\\');
            var separator = windowsPath.LastIndexOf('\\');
            var directory = separator < 0 ? "" : windowsPath[..separator];
            install.AppendLine("SetOutPath " + RuntimePath(directory));
            var name = separator < 0 ? windowsPath : windowsPath[(separator + 1)..];
            install.AppendLine("File " + Quote("/oname=" + name) + $" \"files\\{index:D5}.dat\"");
            remove.AppendLine("Delete " + RuntimePath(windowsPath));
        }
        var directories = files.SelectMany(f => Parents(f.Replace('/', '\\'))).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Count(c => c == '\\')).ThenByDescending(d => d.Length);
        foreach (var directory in directories) removeDirectories.AppendLine("RMDir " + RuntimePath(directory));
        var icon = options.IconPath is null ? "" : "Icon \"installer.ico\"\nUninstallIcon \"installer.ico\"";
        var shortcutIcon = options.IconPath is null ? "$INSTDIR\\NovellMaker.Runtime.exe" : "$INSTDIR\\game.ico";
        var shortcutName = new string(title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).Take(80).ToArray()).TrimEnd('.', ' ');
        return $$"""
            Unicode true
            ManifestSupportedOS Win10
            RequestExecutionLevel user
            SetCompressor /SOLID zlib
            CRCCheck force
            AllowSkipFiles off
            Name {{Quote(title)}} {{Quote(title.Replace("&", "&&", StringComparison.Ordinal))}}
            OutFile "novell-package.exe"
            InstallDir "$LOCALAPPDATA\Programs\NovellMakerGames\{{id}}"
            VIProductVersion {{Quote(version)}}
            VIAddVersionKey /LANG=1049 "ProductName" {{Quote(title, runtimeVariables: false)}}
            VIAddVersionKey /LANG=1049 "FileDescription" "Установка игры"
            VIAddVersionKey /LANG=1049 "LegalCopyright" ""
            VIAddVersionKey /LANG=1049 "FileVersion" {{Quote(version)}}
            VIAddVersionKey /LANG=1049 "ProductVersion" {{Quote(options.Version)}}
            {{icon}}
            !include "MUI2.nsh"
            !include "x64.nsh"
            !define MUI_ABORTWARNING
            !insertmacro MUI_PAGE_WELCOME
            !insertmacro MUI_PAGE_DIRECTORY
            !insertmacro MUI_PAGE_INSTFILES
            !insertmacro MUI_PAGE_FINISH
            !insertmacro MUI_UNPAGE_CONFIRM
            !insertmacro MUI_UNPAGE_INSTFILES
            !insertmacro MUI_UNPAGE_FINISH
            !insertmacro MUI_LANGUAGE "Russian"

            Function .onInit
              SetShellVarContext current
              ${IfNot} ${RunningX64}
                MessageBox MB_OK|MB_ICONSTOP "Для этой игры нужна Windows x64." /SD IDOK
                SetErrorLevel 2
                Quit
              ${EndIf}
              SetRegView 64
              ReadRegStr $0 HKCU "{{key}}" "InstallLocation"
              StrCmp $0 "" ready
                MessageBox MB_OK|MB_ICONSTOP "Эта игра уже установлена. Сначала удалите прежнюю версию через параметры Windows. Сохранения останутся в профиле игрока." /SD IDOK
                SetErrorLevel 2
                Quit
              ready:
            FunctionEnd

            ; Never merge with an existing user's directory, including in silent mode.
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

            Section "Игра"
              Call CheckEmptyDirectory
              IfErrors 0 install
                MessageBox MB_OK|MB_ICONSTOP "Выберите новую пустую папку для игры." /SD IDOK
                SetErrorLevel 2
                Quit
              install:
              ClearErrors
              SetOutPath "$INSTDIR"
              IfErrors failed
              WriteINIStr "$INSTDIR\novell-install.ini" "Install" "ProjectId" "{{id}}"
              IfErrors failed
              {{install}}
              IfErrors failed
              WriteUninstaller "$INSTDIR\Uninstall.exe"
              IfErrors failed
              CreateDirectory "$SMPROGRAMS\NovellMaker Games\{{id}}"
              CreateShortcut {{"\"$SMPROGRAMS\\NovellMaker Games\\" + id + "\\" + Quote(shortcutName)[1..^1] + ".lnk\""}} "$INSTDIR\NovellMaker.Runtime.exe" "" "{{shortcutIcon}}" 0 SW_SHOWNORMAL "" {{Quote(title)}}
              CreateShortcut "$SMPROGRAMS\NovellMaker Games\{{id}}\Удалить игру.lnk" "$INSTDIR\Uninstall.exe"
              WriteRegStr HKCU "{{key}}" "DisplayName" {{Quote(title)}}
              WriteRegStr HKCU "{{key}}" "DisplayVersion" {{Quote(options.Version)}}
              WriteRegStr HKCU "{{key}}" "InstallLocation" "$INSTDIR"
              WriteRegStr HKCU "{{key}}" "DisplayIcon" "{{shortcutIcon}}"
              WriteRegStr HKCU "{{key}}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
              WriteRegStr HKCU "{{key}}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
              WriteRegDWORD HKCU "{{key}}" "NoModify" 1
              WriteRegDWORD HKCU "{{key}}" "NoRepair" 1
              IfErrors failed
              Goto done
              failed:
                MessageBox MB_OK|MB_ICONSTOP "Установка не завершена. Проверьте свободное место и права записи в выбранную папку." /SD IDOK
                SetErrorLevel 3
                Abort
              done:
            SectionEnd

            Function un.onInit
              SetShellVarContext current
              SetRegView 64
              ReadINIStr $0 "$INSTDIR\novell-install.ini" "Install" "ProjectId"
              StrCmp $0 "{{id}}" 0 invalid
              ReadRegStr $0 HKCU "{{key}}" "InstallLocation"
              StrCmp $0 "$INSTDIR" valid
              invalid:
                MessageBox MB_OK|MB_ICONSTOP "Не найдена установленная копия этой игры. Удаление отменено." /SD IDOK
                SetErrorLevel 2
                Quit
              valid:
            FunctionEnd

            Function .onInstFailed
              ReadINIStr $0 "$INSTDIR\novell-install.ini" "Install" "ProjectId"
              StrCmp $0 "{{id}}" 0 done
              {{remove}}
              {{removeDirectories}}
              Delete {{"\"$SMPROGRAMS\\NovellMaker Games\\" + id + "\\" + Quote(shortcutName)[1..^1] + ".lnk\""}}
              Delete "$SMPROGRAMS\NovellMaker Games\{{id}}\Удалить игру.lnk"
              RMDir "$SMPROGRAMS\NovellMaker Games\{{id}}"
              RMDir "$SMPROGRAMS\NovellMaker Games"
              ReadRegStr $0 HKCU "{{key}}" "InstallLocation"
              StrCmp $0 "$INSTDIR" 0 keepRegistry
              DeleteRegKey HKCU "{{key}}"
              keepRegistry:
              Delete "$INSTDIR\novell-install.ini"
              Delete "$INSTDIR\Uninstall.exe"
              SetOutPath "$TEMP"
              RMDir "$INSTDIR"
              done:
            FunctionEnd

            Section "Uninstall"
              ClearErrors
              {{remove}}
              IfErrors failed
              {{removeDirectories}}
              Delete {{"\"$SMPROGRAMS\\NovellMaker Games\\" + id + "\\" + Quote(shortcutName)[1..^1] + ".lnk\""}}
              Delete "$SMPROGRAMS\NovellMaker Games\{{id}}\Удалить игру.lnk"
              RMDir "$SMPROGRAMS\NovellMaker Games\{{id}}"
              RMDir "$SMPROGRAMS\NovellMaker Games"
              DeleteRegKey HKCU "{{key}}"
              Delete "$INSTDIR\novell-install.ini"
              Delete "$INSTDIR\Uninstall.exe"
              SetOutPath "$TEMP"
              RMDir "$INSTDIR"
              ; Save files live outside INSTDIR and are deliberately preserved.
              Goto done
              failed:
                MessageBox MB_OK|MB_ICONSTOP "Не все файлы удалены. Закройте игру и повторите удаление. Сохранения не затронуты." /SD IDOK
                SetErrorLevel 3
                Abort
              done:
            SectionEnd
            """;
    }

    private static string RuntimePath(string? relative) => "\"$INSTDIR" + (string.IsNullOrEmpty(relative) ? "" : "\\" + Quote(relative)[1..^1]) + "\"";
    private static IEnumerable<string> Parents(string file)
    {
        var separator = file.LastIndexOf('\\');
        while (separator >= 0) { file = file[..separator]; yield return file; separator = file.LastIndexOf('\\'); }
    }
}
