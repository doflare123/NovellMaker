using NovellMaker.Core.Projects;
using NovellMaker.Core.Styling;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.Services.Export;

/// <summary>Packages authoring data with a prebuilt self-contained host. Never invokes an SDK.</summary>
public sealed class WindowsExportService(string templateDirectory, IImageAssetService images)
{
    public static string DefaultTemplateDirectory => Path.Combine(AppContext.BaseDirectory, "ExportTemplates", "win-x64");

    public async Task ExportAsync(NovelProject project, string assetsRoot, Guid startSceneId, string destination)
    {
        _ = project.ReleaseCheck?.ToConfiguration();
        DialogueDisplayValidation.Validate(project);
        await Task.Run(() => NovellMaker.Core.Scripting.LuaModuleLibrary.ValidateProject(project));
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Каталог сборки уже существует. Выберите новое имя; готовые сборки не перезаписываются.");
        var startScene = project.Scenes.FirstOrDefault(s => s.Id == startSceneId)
            ?? throw new ArgumentException("Выберите стартовую сцену игры.");
        if (startScene.Lines.IsEmpty && !startScene.HasActionScript && !startScene.HasPresentation)
            throw new ArgumentException("В стартовой сцене нет компонентов, задника, фраз или скрипта.");
        var runtime = RuntimeTemplateManifest.Validate(templateDirectory);
        _ = DialogueCssParser.Parse(project.DialogueUi.Css);
        foreach (var scene in project.Scenes)
            if (scene.DialogueCssOverride is { } css) _ = DialogueCssParser.Parse(css);

        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Выберите каталог сборки.");
        var templateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(templateDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent.Equals(templateRoot, comparison) || parent.StartsWith(templateRoot + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Папка сборки не может находиться внутри шаблона runtime.");
        Directory.CreateDirectory(parent);
        // Staging is owned by this operation. A failed export cannot expose a half-built game.
        var staging = Path.Combine(parent, ".novell-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await new JsonProjectStore().SaveAsync(Path.Combine(staging, GamePackage.ProjectName), project);
            await images.CopyAsync(assetsRoot, staging, project.Images);
            await AudioAssetService.CopyAsync(assetsRoot, staging, project.Audio.Assets);
            runtime.CopyTo(staging);
            await new GamePackage(GamePackage.CurrentVersion, project.Id, startSceneId, project.DefaultLanguage, project.Name)
                { StartScreenSceneId = project.StartScreenSceneId }.SaveAsync(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "Как играть.txt"),
                $"{project.Name}\n\nЗапустите NovellMaker.Runtime.exe. Переносите всю папку целиком.\n" +
                "Windows x64; редактор, SDK и отдельная установка .NET не нужны.\n" +
                "Enter/Space/→ — продолжить, мышь — выбор/предмет, Escape — выход.\n" +
                "Клавиши и интерфейс задаются проектом. Сохранения: %LOCALAPPDATA%/NovellMaker/Games/" + project.Id +
                "/quicksave.json\nДиагностика запуска: там же startup-error.log.\nСохранения требуют точной редакции проекта.\n");
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging) && Path.GetDirectoryName(Path.GetFullPath(staging)) == parent &&
                Path.GetFileName(staging).StartsWith(".novell-export-", StringComparison.Ordinal))
                Directory.Delete(staging, recursive: true);
        }
    }

}
