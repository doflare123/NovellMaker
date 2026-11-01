using NovellMaker.Core.Characters;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    /// <summary>Commits one complete visual edit through the normal undo/recovery/F6 snapshot path.</summary>
    public void SetCharacterVisual(CharacterVisual? visual)
    {
        if (!CanEditState || project is null || selectedCharacter is null || selectedState is null) return;
        visual?.Validate();
        if (visual is null ? selectedState.Model.Visual is null : visual.ContentEquals(selectedState.Model.Visual)) return;
        var updated = selectedCharacter.Model with
        {
            States = selectedCharacter.Model.States.SetItem(FindStateIndex(selectedCharacter.Model, selectedState.Id),
                selectedState.Model with { Visual = visual })
        };
        var candidate = project with { Characters = project.Characters.SetItem(FindCharacterIndex(updated.Id), updated) };
        if (selectedState.Model.Visual?.Clips.Any(c => visual?.Clips.Any(v => v.Id == c.Id) != true) == true)
            candidate = Services.ActionReferences.Prune(candidate);
        EditProject(candidate);
    }

    public Task AddCharacterAnimationExampleAsync() => RunAsync(async () =>
    {
        if (project is null) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var added = Examples.ExampleCharacterAnimationModule.AddTo(project, ProjectDirectory!);
        EditProject(added);
        OpenScene(added.Scenes[^1].Id);
        SelectCharacter(added.Characters[^1].Id);
        Log("Добавлен персонаж из частей и сцена с двумя представлениями. Выберите сцену и нажмите F6.");
        await UpdateGitAsync();
    });

    public Task ImportCharacterGifAsync() => RunAsync(async () =>
    {
        if (project is null || selectedCharacter is null || selectedState is null) return;
        var source = await dialogs.PickGifAsync();
        if (source is null) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var result = await Services.Assets.CharacterGifImporter.ImportAsync(ProjectDirectory!, source);
        var visual = new CharacterVisual(result.Width, result.Height, [], [result.Clip], result.Clip.Id);
        var character = selectedCharacter.Model;
        var state = selectedState.Model with { ImageId = result.Images[0].Id, Visual = visual };
        var updated = character with { States = character.States.SetItem(FindStateIndex(character, state.Id), state) };
        EditProject(Services.ActionReferences.Prune(project with { Images = project.Images.AddRange(result.Images), Characters = project.Characters.SetItem(FindCharacterIndex(character.Id), updated) }));
        Log($"GIF: импортировано кадров — {result.Images.Length}.");
        await UpdateGitAsync();
    });
}
