using System.Text.Json;
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
        DialogueDisplayValidation.Validate(project);
        await Task.Run(() => NovellMaker.Core.Scripting.LuaModuleLibrary.ValidateProject(project));
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Каталог сборки уже существует. Выберите новое имя; готовые сборки не перезаписываются.");
        var startScene = project.Scenes.FirstOrDefault(s => s.Id == startSceneId)
            ?? throw new ArgumentException("Выберите стартовую сцену игры.");
        if (startScene.Lines.IsEmpty && !startScene.HasActionScript && !startScene.HasPresentation)
            throw new ArgumentException("В стартовой сцене нет компонентов, задника, фраз или скрипта.");
        ValidateTemplate(templateDirectory);
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
            foreach (var path in Directory.EnumerateFiles(templateDirectory, "*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(templateDirectory, path);
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: false);
            }
            await new GamePackage(2, project.Id, startSceneId, project.DefaultLanguage, project.Name)
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

    internal static void ValidateTemplate(string directory)
    {
        foreach (var file in new[] { "NovellMaker.Runtime.exe", "NovellMaker.Runtime.dll", "NovellMaker.Core.dll",
                     "NovellMaker.Runtime.deps.json", "NovellMaker.Runtime.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "SDL2.dll", "openal.dll", "libSkiaSharp.dll" })
            if (!File.Exists(Path.Combine(directory, file)))
                throw new IOException($"Не найден автономный шаблон Windows x64 ({file}). Подготовьте поставку редактора скриптом scripts/prepare-windows-export.ps1.");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "NovellMaker.Runtime.runtimeconfig.json")));
        var options = config.RootElement.GetProperty("runtimeOptions");
        if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _) || !options.TryGetProperty("includedFrameworks", out _))
            throw new IOException("Шаблон Windows должен включать .NET (self-contained).");
    }
}
