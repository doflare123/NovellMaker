using NovellMaker.Editor.Services.Git;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private ExplorerEntry? fileExplorerRoot;
    private string? fileExplorerPath;
    private GitStatusSnapshot? fileGitSnapshot;

    public bool IsFolderExplorer => project?.ExplorerShowsFiles == true;

    public int ExplorerModeIndex
    {
        get => project?.ExplorerShowsFiles == true ? 1 : 0;
        set
        {
            if (!CanEdit || project is null || value is < 0 or > 1) return;
            fileExplorerRoot = null;
            EditProject(project with { ExplorerShowsFiles = value == 1 });
        }
    }

    private void RebuildFileExplorer()
    {
        if (project is null) return;
        // Logical documents live inside .novell; merge them with the physical resource tree.
        // Rebuild their entries on authoring changes while retaining the cached disk listing.
        if (fileExplorerRoot is null || fileExplorerPath != projectPath)
        {
            fileExplorerPath = projectPath;
            var directory = projectPath is null ? null : Path.GetDirectoryName(projectPath)!;
            fileExplorerRoot = directory is null
                ? new ExplorerEntry("Файлы проекта", "Сохраните проект, чтобы выбрать рабочую папку.",
                    [new ExplorerEntry("Параметры проекта — сохраните проект", "", [], IsProjectSettings: true)])
                : new ExplorerEntry(Path.GetFileName(directory), directory, [], ChildLoader: () => ReadProjectDirectory(directory));
        }
        var scenes = new ExplorerEntry("Сцены/", "Сцены внутри файла .novell", project.Scenes.Select(scene =>
            new ExplorerEntry(scene.Name + "/", scene.Id.ToString(),
                [new ExplorerEntry("Сценарий", $"Реплик: {scene.Lines.Length}", [], SceneId: scene.Id)],
                SceneId: scene.Id, ExpandInitially: false, IsDirectory: true)).ToArray(), ExpandInitially: false, IsDirectory: true);
        var characters = new ExplorerEntry("Персонажи/", "Персонажи внутри файла .novell", project.Characters.Select(character =>
            new ExplorerEntry(character.Name + "/", character.Id.ToString(), character.States.Select(state =>
                new ExplorerEntry(state.Name, state.Id.ToString(), [], CharacterId: character.Id, CharacterStateId: state.Id)).ToArray(),
                CharacterId: character.Id, ExpandInitially: false, IsDirectory: true)).ToArray(),
            IsCharacterLibrary: true, ExpandInitially: false, IsDirectory: true);
        var scripts = new ExplorerEntry("Lua-скрипты/", "Lua-ресурсы внутри .novell", project.Scripts.Select(script =>
            new ExplorerEntry(script.Name, script.Id.ToString(), [], ScriptId: script.Id)).ToArray(), IsScriptLibrary: true, ExpandInitially: false, IsDirectory: true);
        var disk = fileExplorerRoot.Items.Select(entry => entry.IsDirectory && entry.Label is "Сцены/" or "Персонажи/" or "Lua-скрипты/"
            ? entry with { Label = entry.Label + " (файлы)" } : entry);
        var view = new ExplorerEntry(ProjectName, projectPath ?? "Несохранённый проект", [scenes, characters, scripts, .. disk], IsDirectory: true);
        if (Explorer.FirstOrDefault() is { } previous) PreserveFolderExpansion(view, previous);
        if (Explorer.Count == 0) Explorer.Add(view);
        else Explorer[0] = view;
    }

    private static void PreserveFolderExpansion(ExplorerEntry current, ExplorerEntry previous)
    {
        current.IsExpanded = previous.IsExpanded;
        foreach (var child in current.Children)
        {
            var old = previous.Children.FirstOrDefault(entry =>
                child.SceneId is { } sceneId ? entry.SceneId == sceneId && entry.IsDirectory == child.IsDirectory :
                child.ScriptId is { } scriptId ? entry.ScriptId == scriptId :
                child.CharacterId is { } characterId && child.IsDirectory ? entry.CharacterId == characterId && entry.IsDirectory :
                entry.Label == child.Label);
            if (old is not null && !ReferenceEquals(child, old)) PreserveFolderExpansion(child, old);
        }
    }

    private IReadOnlyList<ExplorerEntry> ReadProjectDirectory(string directory)
    {
        try
        {
            var result = new List<ExplorerEntry>();
            var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(info => info.Name != ".git")
                .OrderByDescending(info => info is DirectoryInfo)
                .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var info in entries)
            {
                var fullPath = info.FullName;
                var isFolder = info is DirectoryInfo;
                var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                var image = project?.Images.FirstOrDefault(asset => string.Equals(
                    Path.GetFullPath(Path.Combine(ProjectDirectory!, asset.RelativePath)), fullPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                var change = fileGitSnapshot?.Changes.FirstOrDefault(item => fileGitSnapshot.RepositoryRoot is { } root &&
                    string.Equals(Path.GetFullPath(Path.Combine(root, item.Path)), fullPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                var badge = change is null ? "" : $"{change.IndexStatus}{change.WorkTreeStatus}  ";
                var script = project?.Scripts.FirstOrDefault(s => string.Equals(Path.Combine(ProjectDirectory!, "Scripts", s.Id.ToString("N") + ".lua"), fullPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                result.Add(new ExplorerEntry(badge + (script?.Name ?? info.Name) + (isFolder ? "/" : ""),
                    fullPath + (isLink ? " (ссылка; обход отключён)" : ""), [], ImageAssetId: image?.Id,
                    FilePath: fullPath, IsProjectSettings: fullPath == projectPath, ExpandInitially: false, IsDirectory: isFolder,
                    ChildLoader: isFolder && !isLink ? () => ReadProjectDirectory(fullPath) : null, GitStatus: badge.Trim(), ScriptId: script?.Id));
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [new ExplorerEntry("Не удалось прочитать папку", error.Message)];
        }
    }
}
