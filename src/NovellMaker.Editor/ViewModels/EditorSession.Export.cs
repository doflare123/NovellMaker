using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand ExportWindowsCommand { get; private set; } = null!;
    public AsyncCommand ExportWindowsInstallerCommand { get; private set; } = null!;

    private void InitializeExport()
    {
        bool CanExport() => CanEdit && IsNameValid && AreDocumentsValid && project!.Scenes.Length > 0;
        ExportWindowsCommand = new(ExportWindowsAsync, CanExport);
        ExportWindowsInstallerCommand = new(ExportWindowsInstallerAsync, () => OperatingSystem.IsWindows() && CanExport());
    }

    public Task ExportWindowsInstallerAsync() => RunAsync(async () =>
    {
        if (project is null || !IsNameValid || !AreDocumentsValid) return;
        Services.DialogueDisplayValidation.Validate(project);
        await ValidateExternalScriptsForRunAsync();
        var destination = await dialogs.PickWindowsInstallerAsync(project);
        if (destination is null) return;
        var snapshot = project;
        var assetsRoot = ProjectDirectory ?? Path.GetTempPath();
        Log("Сборка установщика EXE: проверка игры и упаковка зависимостей…");
        var exporter = new WindowsExportService(WindowsExportService.DefaultTemplateDirectory, imageAssets);
        await Task.Run(() => new WindowsInstallerService(exporter, WindowsInstallerService.DefaultCompilerDirectory)
            .ExportAsync(snapshot, assetsRoot, destination.StartSceneId, destination.FilePath, destination.Options));
        Log("Установщик игры собран: " + destination.FilePath);
        await dialogs.ShowInformationAsync("Установщик готов", "Передайте игроку этот EXE:\n" + destination.FilePath +
            "\n\nОн установит игру и зависимости без редактора, SDK или отдельной установки .NET. Сохранения останутся в профиле игрока. Папку ExportTemplates передавать не нужно.");
    });

    public Task ExportWindowsAsync() => RunAsync(async () =>
    {
        if (project is null || !IsNameValid || !AreDocumentsValid) return;
        Services.DialogueDisplayValidation.Validate(project);
        await ValidateExternalScriptsForRunAsync();
        var destination = await dialogs.PickWindowsExportAsync(project);
        if (destination is null) return;
        var snapshot = project;
        Log("Сборка Windows x64: проверка проекта и копирование автономной игры…");
        var exporter = new WindowsExportService(WindowsExportService.DefaultTemplateDirectory, imageAssets);
        var assetsRoot = ProjectDirectory ?? Path.GetTempPath();
        await Task.Run(() => exporter.ExportAsync(snapshot, assetsRoot, destination.StartSceneId, destination.Directory));
        Log("Игра Windows x64 собрана: " + destination.Directory);
        await dialogs.ShowInformationAsync("Игра собрана", "Папка игры:\n" + destination.Directory +
            "\n\nЗапустите NovellMaker.Runtime.exe. Для передачи игроку скопируйте всю папку.\nДля одного передаваемого файла выберите «Сборка → Установщик игры EXE…».");
    });
}
