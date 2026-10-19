using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand ExportWindowsCommand { get; private set; } = null!;

    private void InitializeExport() => ExportWindowsCommand = new(ExportWindowsAsync,
        () => CanEdit && IsNameValid && AreDocumentsValid && project!.Scenes.Length > 0);

    public Task ExportWindowsAsync() => RunAsync(async () =>
    {
        if (project is null || !IsNameValid || !AreDocumentsValid) return;
        var destination = await dialogs.PickWindowsExportAsync(project);
        if (destination is null) return;
        var snapshot = project;
        Log("Сборка Windows x64: проверка проекта и копирование автономной игры…");
        var exporter = new WindowsExportService(WindowsExportService.DefaultTemplateDirectory, imageAssets);
        var assetsRoot = ProjectDirectory ?? Path.GetTempPath();
        await Task.Run(() => exporter.ExportAsync(snapshot, assetsRoot, destination.StartSceneId, destination.Directory));
        Log("Игра Windows x64 собрана: " + destination.Directory);
        await dialogs.ShowInformationAsync("Игра собрана", "Папка игры:\n" + destination.Directory +
            "\n\nЗапустите NovellMaker.Runtime.exe. Для передачи игроку скопируйте всю папку.\nЭто автономная сборка; установщик EXE — следующий этап.");
    });
}
