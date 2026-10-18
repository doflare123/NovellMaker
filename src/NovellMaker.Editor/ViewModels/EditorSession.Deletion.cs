using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public async Task DeleteActionAsync(Guid sceneId, Guid actionId)
    {
        if (!CanEdit || project is null) return;
        var before = project;
        var scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId);
        var action = scene?.Actions.Concat(scene.ScriptBlocks.Select(b => b.Action)).FirstOrDefault(a => a.Id == actionId);
        if (scene is null || action is null || !await dialogs.ConfirmDeleteAsync("Действие сценария", "Действие и прямые переходы на него будут удалены. Текст Lua нужно проверить отдельно. Ctrl+Z отменяет удаление.")) return;
        if (!ReferenceEquals(project, before)) return;
        EditProject(ActionReferences.Prune(project with { Scenes = project.Scenes.Replace(scene, scene with { Actions = scene.Actions.Remove(action), ScriptBlocks = scene.ScriptBlocks.Where(b => b.Action.Id != actionId).ToImmutableArray() }) }));
    }
    private ImmutableArray<ProjectFileDeletion> deletedFiles = [];
    private sealed record DeletionPlan(string Name, string Message, NovelProject Updated, string? FilePath = null);

    public Task DeleteSceneAsync(Guid id) => DeleteExplorerEntryAsync(new ExplorerEntry("", "", [], SceneId: id));
    public Task DeleteCharacterAsync(Guid id) => DeleteExplorerEntryAsync(new ExplorerEntry("", "", [], CharacterId: id));
    public Task DeleteStateAsync(Guid character, Guid state) => DeleteExplorerEntryAsync(new ExplorerEntry("", "", [], CharacterId: character, CharacterStateId: state));
    public Task DeleteImageAsync(Guid id) => DeleteExplorerEntryAsync(new ExplorerEntry("", "", [], ImageAssetId: id));

    public Task DeleteExplorerEntryAsync(ExplorerEntry entry) => RunAsync(async () =>
    {
        if (project is null) return;
        var before = project;
        var pathBefore = projectPath;
        var plan = PlanDeletion(entry);
        if (plan is null) return;
        if (!await dialogs.ConfirmDeleteAsync(plan.Name, plan.Message + "\n\nОтменить удаление можно через Ctrl+Z в текущем сеансе.")) return;
        if (!ReferenceEquals(project, before) || projectPath != pathBefore)
            throw new IOException("Проект изменился во время подтверждения. Повторите удаление.");
        ProjectFileDeletion? file = null;
        if (plan.FilePath is { } path && (File.Exists(path) || Directory.Exists(path)))
            file = await Task.Run(() => ProjectFileDeletion.Create(ProjectDirectory!, path, projectPath!));
        RecordEdit(null);
        project = ActionReferences.Prune(plan.Updated);
        if (file is not null) deletedFiles = deletedFiles.Add(file);
        assetRevision++; fileExplorerRoot = null;
        NotifyProjectChanged();
        Log($"Удалён объект «{plan.Name}».");
        await UpdateGitAsync();
    });

    private DeletionPlan? PlanDeletion(ExplorerEntry entry)
    {
        if (project is null) return null;
        if (entry.CharacterStateId is { } stateId && entry.CharacterId is { } owner)
        {
            var character = project.Characters.FirstOrDefault(c => c.Id == owner);
            var state = character?.States.FirstOrDefault(s => s.Id == stateId);
            if (character is null || state is null) return null;
            return new(state.Name, "Состояние будет удалено. Команды его выбора в репликах будут сброшены; исходное состояние будет выбрано из оставшихся.",
                RemoveStates(project, [stateId]));
        }
        if (entry.SceneId is { } sceneId)
        {
            var scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId);
            return scene is null ? null : new(scene.Name, $"Сцена и все её реплики ({scene.Lines.Length}) будут удалены из проекта.",
                project with { Scenes = project.Scenes.Remove(scene) });
        }
        if (entry.CharacterId is { } characterId)
        {
            var character = project.Characters.FirstOrDefault(c => c.Id == characterId);
            if (character is null) return null;
            var updated = RemoveStates(project, character.States.Select(s => s.Id).ToHashSet());
            updated = updated with
            {
                Characters = updated.Characters.Where(c => c.Id != characterId).ToImmutableArray(),
                Scenes = updated.Scenes.Select(scene => scene with { Lines = scene.Lines.Select(line => line with
                {
                    SpeakerId = line.SpeakerId == characterId ? null : line.SpeakerId,
                    CharacterStateId = line.SpeakerId == characterId ? null : line.CharacterStateId,
                    HideCharacterId = line.HideCharacterId == characterId ? null : line.HideCharacterId,
                    Placements = line.Placements.Where(p => p.CharacterId != characterId).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray()
            };
            return new(character.Name, "Персонаж и его состояния будут удалены. Реплики останутся; говорящий, скрытие и размещение этого персонажа будут сброшены. Файлы изображений сохранятся.", updated);
        }
        if (entry.ImageAssetId is { } imageId)
        {
            var image = project.Images.FirstOrDefault(i => i.Id == imageId);
            if (image is null) return null;
            var path = ProjectDirectory is null ? null : NovellMaker.Core.Assets.AssetPath.Resolve(ProjectDirectory, image.RelativePath);
            return new(image.Name, "Изображение будет удалено из библиотеки и с диска. Задники с этим изображением будут сброшены; использующие его состояния персонажей и команды этих состояний будут удалены.",
                RemoveImages(project, [imageId]), path);
        }
        if (entry.FilePath is { } filePath && ProjectDirectory is { } root && projectPath is { } projectFile)
        {
            var full = ProjectFileDeletion.Validate(root, filePath, projectFile);
            var directory = Directory.Exists(full);
            var images = project.Images.Where(image => directory
                ? ProjectFileDeletion.IsWithin(full, Path.GetFullPath(Path.Combine(root, image.RelativePath)))
                : string.Equals(full, Path.GetFullPath(Path.Combine(root, image.RelativePath)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .Select(image => image.Id).ToHashSet();
            return new(Path.GetFileName(full), directory ? "Папка и всё её содержимое будут удалены с диска. Зарегистрированные изображения в этой папке и их привязки будут удалены из проекта."
                : "Файл будет удалён с диска. Будет сохранена резервная копия для отмены.", RemoveImages(project, images), full);
        }
        // Project roots, virtual category folders and Git rows are not deletable objects.
        return null;
    }

    private static NovelProject RemoveImages(NovelProject source, HashSet<Guid> ids)
    {
        var states = source.Characters.SelectMany(c => c.States).Where(s => ids.Contains(s.ImageId)).Select(s => s.Id).ToHashSet();
        var updated = RemoveStates(source, states);
        return updated with
        {
            Images = updated.Images.Where(i => !ids.Contains(i.Id)).ToImmutableArray(),
            Scenes = updated.Scenes.Select(scene => scene.BackgroundImageId is { } id && ids.Contains(id)
                ? scene with { BackgroundImageId = null } : scene).ToImmutableArray()
        };
    }
    private static NovelProject RemoveStates(NovelProject source, HashSet<Guid> ids) => source with
    {
        Characters = source.Characters.Select(character =>
        {
            var states = character.States.Where(state => !ids.Contains(state.Id)).ToImmutableArray();
            return character with { States = states, DefaultStateId = character.DefaultStateId is { } id && ids.Contains(id) ? states.FirstOrDefault()?.Id : character.DefaultStateId };
        }).ToImmutableArray(),
        Scenes = source.Scenes.Select(scene => scene with
        {
            Lines = scene.Lines.Select(line => line.CharacterStateId is { } id && ids.Contains(id) ? line with { CharacterStateId = null } : line).ToImmutableArray()
        }).ToImmutableArray()
    };

    internal Task ConfirmEditorDeletionAsync(string name, Action delete) => RunAsync(async () =>
    {
        var before = project;
        if (await dialogs.ConfirmDeleteAsync(name, "Удаление можно отменить через Ctrl+Z.") && ReferenceEquals(project, before)) delete();
    });

    private void RestoreDeletedFiles(ImmutableArray<ProjectFileDeletion> target)
    {
        if (deletedFiles.SequenceEqual(target)) return;
        foreach (var item in deletedFiles.Reverse().Where(item => !target.Contains(item))) item.Restore(projectPath!);
        foreach (var item in target.Where(item => !deletedFiles.Contains(item))) item.DeleteOriginal(projectPath!);
        deletedFiles = target;
        fileExplorerRoot = null; assetRevision++;
    }
    private async Task RunHistoryAsync(Action action)
    {
        var filesBefore = deletedFiles;
        try { action(); if (ProjectPath is not null && !filesBefore.SequenceEqual(deletedFiles)) await RefreshGitAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log(error.Message); await dialogs.ShowErrorAsync("Не удалось отменить или повторить действие", error.Message); }
    }
}
