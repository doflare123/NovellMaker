using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Projects;

internal static partial class ProjectJsonCodec
{
    private static Version20ProjectDocument MapForSave(NovelProject project) => new()
    {
        ReleaseCheck = project.ReleaseCheck,
        StartScreenSceneId = project.StartScreenSceneId,
        InputBindings = project.InputBindings,
        Scripts = project.Scripts,
        Audio = project.Audio,
        ExplorerShowsFiles = project.ExplorerShowsFiles,
        FormatVersion = project.FormatVersion,
        Id = project.Id,
        Name = project.Name,
        Scenes = project.Scenes.Select(scene => (Version13SceneDocument?)MapSceneForSave(scene)).ToList(),
        Images = project.Images.Select(image => (ImageDocument?)MapImageForSave(image)).ToList(),
        Characters = project.Characters.Select(character => (CharacterDocument?)MapCharacterForSave(character)).ToList(),
        DefaultLanguage = project.DefaultLanguage,
        DialogueUi = new DialogueUiDocument
        {
            Css = project.DialogueUi.Css,
            ShowVoiceProgress = project.DialogueUi.ShowVoiceProgress,
            ShowVoiceControls = project.DialogueUi.ShowVoiceControls,
            ShowNavigationControls = project.DialogueUi.ShowNavigationControls,
        },
    };

    private static Version13SceneDocument MapSceneForSave(NovelScene scene) => new()
    {
        Id = scene.Id,
        Name = scene.Name,
        Lines = scene.Lines.Select(line => (Version5LineDocument?)MapLineForSave(line)).ToList(),
        DialogueCssOverride = scene.DialogueCssOverride,
        BackgroundImageId = scene.BackgroundImageId,
        Actions = scene.Actions,
        UseActions = scene.UseActions,
        ScriptBlocks = scene.ScriptBlocks,
        Canvas = scene.Canvas,
    };

    private static Version5LineDocument MapLineForSave(DialogueLine line)
    {
        var texts = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (language, text) in line.Texts)
        {
            texts.Add(language, text);
        }

        return new Version5LineDocument
        {
            Id = line.Id,
            TextKey = line.TextKey,
            Texts = texts,
            SpeakerId = line.SpeakerId,
            CharacterStateId = line.CharacterStateId,
            HideCharacterId = line.HideCharacterId,
            Placements = line.Placements.Select(command => (PlacementCommandDocument?)new PlacementCommandDocument
            {
                CharacterId = command.CharacterId,
                Placement = command.Placement is null ? null : new PlacementDocument
                { X = command.Placement.X, Y = command.Placement.Y, Scale = command.Placement.Scale, Layer = command.Placement.Layer },
            }).ToList(),
        };
    }

    private static ImageDocument MapImageForSave(ImageAsset image) => new()
    {
        Id = image.Id,
        Name = image.Name,
        RelativePath = image.RelativePath,
        PixelWidth = image.PixelWidth,
        PixelHeight = image.PixelHeight,
    };

    private static CharacterDocument MapCharacterForSave(NovelCharacter character) => new()
    {
        Id = character.Id,
        Name = character.Name,
        States = character.States.Select(state => (CharacterStateDocument?)new CharacterStateDocument
        {
            Id = state.Id,
            Name = state.Name,
            ImageId = state.ImageId,
        }).ToList(),
        DefaultStateId = character.DefaultStateId,
    };

}
