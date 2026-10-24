using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Projects;

internal static partial class ProjectJsonCodec
{
    private static NovelProject MigrateFromVersion1(Version1ProjectDocument document) =>
        new(NovelProject.CurrentFormatVersion, document.Id, document.Name!);

    private static NovelProject MigrateFromVersion2(Version2ProjectDocument document)
    {
        var scenes = MapScenes(document.Scenes, MapVersion2Scene);
        return CreateProject(
            document.Id,
            document.Name,
            scenes,
            [],
            [],
            document.DefaultLanguage,
            document.DialogueUi);
    }

    private static NovelProject MigrateFromVersion3(Version3ProjectDocument document)
    {
        var scenes = MapScenes(document.Scenes, MapVersion3Scene);
        if (document.Images is null)
        {
            throw new ArgumentException("Коллекция изображений проекта отсутствует.");
        }

        var images = ImmutableArray.CreateBuilder<ImageAsset>(document.Images.Count);
        foreach (var imageDocument in document.Images)
        {
            images.Add(MapImage(imageDocument ?? throw new ArgumentException("Коллекция изображений содержит null.")));
        }

        return CreateProject(
            document.Id,
            document.Name,
            scenes,
            images.MoveToImmutable(),
            [],
            document.DefaultLanguage,
            document.DialogueUi);
    }

    private static NovelProject MapVersion4(Version4ProjectDocument document)
    {
        var scenes = MapScenes(document.Scenes, MapVersion4Scene);
        var images = MapImages(document.Images);
        if (document.Characters is null)
        {
            throw new ArgumentException("Коллекция персонажей проекта отсутствует.");
        }

        var characters = ImmutableArray.CreateBuilder<NovelCharacter>(document.Characters.Count);
        foreach (var characterDocument in document.Characters)
        {
            characters.Add(MapCharacter(
                characterDocument ?? throw new ArgumentException("Коллекция персонажей содержит null.")));
        }

        return CreateProject(
            document.Id,
            document.Name,
            scenes,
            images,
            characters.MoveToImmutable(),
            document.DefaultLanguage,
            document.DialogueUi);
    }

    private static NovelProject MapVersion5(Version5ProjectDocument document)
    {
        var scenes = MapScenes(document.Scenes, MapVersion5Scene);
        var images = MapImages(document.Images);
        if (document.Characters is null)
        {
            throw new ArgumentException("Коллекция персонажей проекта отсутствует.");
        }

        var characters = ImmutableArray.CreateBuilder<NovelCharacter>(document.Characters.Count);
        foreach (var characterDocument in document.Characters)
        {
            characters.Add(MapCharacter(
                characterDocument ?? throw new ArgumentException("Коллекция персонажей содержит null.")));
        }

        return CreateProject(
            document.Id,
            document.Name,
            scenes,
            images,
            characters.MoveToImmutable(),
            document.DefaultLanguage,
            document.DialogueUi);
    }

    private static NovelProject MapVersion6(Version6ProjectDocument document) =>
        MapVersion5(document) with { ExplorerShowsFiles = document.ExplorerShowsFiles };

    private static NovelProject MapVersion7(Version7ProjectDocument document) =>
        CreateProject(document.Id, document.Name,
            MapScenes(document.Scenes, s => MapVersion5Scene(s) with { Actions = s.Actions ?? throw new ArgumentException("Коллекция действий отсутствует."), UseActions = s.UseActions }),
            MapImages(document.Images),
            document.Characters?.Select(c => MapCharacter(c ?? throw new ArgumentException("Персонаж null."))).ToImmutableArray()
                ?? throw new ArgumentException("Коллекция персонажей отсутствует."),
            document.DefaultLanguage, document.DialogueUi) with { ExplorerShowsFiles = document.ExplorerShowsFiles };

    private static NovelProject MapVersion8(Version8ProjectDocument document) =>
        CreateProject(document.Id, document.Name,
            MapScenes(document.Scenes, s => MapVersion5Scene(s) with { Actions = s.Actions ?? throw new ArgumentException("Нет действий."), UseActions = s.UseActions,
                ScriptBlocks = s.ScriptBlocks ?? throw new ArgumentException("Нет блоков скрипта.") }),
            MapImages(document.Images), document.Characters?.Select(c => MapCharacter(c ?? throw new ArgumentException("Персонаж null."))).ToImmutableArray()
                ?? throw new ArgumentException("Нет персонажей."), document.DefaultLanguage, document.DialogueUi) with { ExplorerShowsFiles = document.ExplorerShowsFiles };

    private static NovelProject MapVersion9(Version9ProjectDocument document) =>
        MapVersion8(document) with { Audio = document.Audio ?? throw new ArgumentException("Нет звука.") };

    private static NovelProject MapVersion10(Version10ProjectDocument document) =>
        MapVersion9(document) with { Scripts = document.Scripts ?? throw new ArgumentException("Нет скриптов.") };

    private static NovelProject MapLegacyVersion11(Version11ProjectDocument document)
    {
        var project = MapVersion11(document);
        if (project.Screens.Any(s => s is not null && !s.Nodes.IsDefault && s.Nodes.Any(n => n is not null && (int)n.Kind > 3)))
            throw new ArgumentException("Версия 11 не поддерживает изображения экранов.");
        return project;
    }

    private static NovelProject MapVersion11(Version11ProjectDocument document)
    {
        var project = MapVersion10(document);
        var screens = document.Screens ?? throw new ArgumentException("Нет экранов.");
        // Validate the old separate namespaces before merging, so a colliding ID cannot overwrite a scene.
        var ids = project.Scenes.Select(s => s.Id).ToHashSet();
        foreach (var screen in screens)
        {
            if (screen is null || !ids.Add(screen.Id)) throw new ArgumentException("Повторный ID сцены или экрана.");
            if (!screen.Nodes.IsDefault && screen.Nodes.Any(n => n is not null && (int)n.Kind > 4))
                throw new ArgumentException("Старый формат не поддерживает интерактивные области.");
            if (!screen.Buttons.IsDefault && screen.Buttons.Any(b => b is not null && (int)b.Action > 5))
                throw new ArgumentException("Старый формат не поддерживает переходы кнопок к сцене.");
        }
        return project with
        {
            Scenes = project.Scenes.AddRange(screens.Select(s => new NovelScene(s.Id, s.Name) { Canvas = SceneCanvas.FromScreen(s) })),
            InputBindings = LegacyInputBindings(document.PauseScreenId)
        };
    }

    private static NovelProject MapVersion13(Version13ProjectDocument document, bool legacy = true)
    {
        if (legacy && document.Scenes?.Any(s => s?.Canvas is { } canvas && !canvas.Nodes.IsDefault && canvas.Nodes.Any(n => n is not null && (int)n.Kind > 4)) == true)
            throw new ArgumentException("Формат 13 не поддерживает этот вид компонента.");
        if (legacy && document.Scenes is { } scenes && scenes.Any(s => s?.Canvas is { } canvas && !canvas.Buttons.IsDefault && canvas.Buttons.Any(b => b is not null && (int)b.Action > 6)))
            throw new ArgumentException("Формат 13 не поддерживает эту команду компонента.");
        return CreateProject(document.Id, document.Name,
            MapScenes(document.Scenes, s => MapVersion5Scene(s) with
            {
                Actions = s.Actions ?? throw new ArgumentException("Нет действий."), UseActions = s.UseActions,
                ScriptBlocks = s.ScriptBlocks ?? throw new ArgumentException("Нет блоков."),
                Canvas = s.Canvas ?? throw new ArgumentException("Нет компонентов сцены.")
            }), MapImages(document.Images),
            document.Characters?.Select(c => MapCharacter(c ?? throw new ArgumentException("Персонаж null."))).ToImmutableArray()
                ?? throw new ArgumentException("Нет персонажей."), document.DefaultLanguage, document.DialogueUi)
        with
        {
            Audio = document.Audio ?? throw new ArgumentException("Нет звука."),
            Scripts = document.Scripts ?? throw new ArgumentException("Нет Lua-ресурсов."),
            ExplorerShowsFiles = document.ExplorerShowsFiles, InputBindings = LegacyInputBindings(document.PauseScreenId)
        };

    }

    private static ImmutableArray<NovellMaker.Core.Screens.ProjectInputBinding> LegacyInputBindings(Guid? pause) => pause is { } id
        ? [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, id)] : [];

    private static NovelProject MapVersion17(Version17ProjectDocument document, bool allowSlots = false) =>
        MapVersion14(document, false, allowSession: true, allowSlots: allowSlots) with { StartScreenSceneId = document.StartScreenSceneId };

    private static NovelProject MapVersion14(Version14ProjectDocument document, bool legacyGeometry = true, bool allowSession = false, bool allowSlots = false)
    {
        if (!allowSlots && (document.InputBindings?.Any(b => b is not null && (int)b.Action > 15) == true ||
            document.Scenes?.Any(s => s?.Canvas?.Buttons.Any(b => b is not null && (int)b.Action > 15) == true) == true))
            throw new ArgumentException("Этот формат не поддерживает команды слотов.");
        if (!allowSession && (document.InputBindings?.Any(b => b is not null && (int)b.Action > 12) == true ||
            document.Scenes?.Any(s => s?.Canvas?.Buttons.Any(b => b is not null && (int)b.Action > 12) == true) == true))
            throw new ArgumentException("Этот формат не поддерживает команды сеанса.");
        if (legacyGeometry && document.Scenes?.Any(s => s?.Canvas is { } canvas && !canvas.Nodes.IsDefault && canvas.Nodes.Any(n => n is not null && (int)n.Kind > 4)) == true)
            throw new ArgumentException("Формат 14 не поддерживает этот вид компонента.");
        return MapVersion13(new Version13ProjectDocument
    {
        Id = document.Id, Name = document.Name, Scenes = document.Scenes, Images = document.Images, Characters = document.Characters,
        Audio = document.Audio, Scripts = document.Scripts, DefaultLanguage = document.DefaultLanguage, DialogueUi = document.DialogueUi,
        ExplorerShowsFiles = document.ExplorerShowsFiles
    }, legacy: false) with { InputBindings = document.InputBindings ?? throw new ArgumentException("Нет назначений клавиш.") };
    }

    private static ImmutableArray<ImageAsset> MapImages(List<ImageDocument?>? imageDocuments)
    {
        if (imageDocuments is null)
        {
            throw new ArgumentException("Коллекция изображений проекта отсутствует.");
        }

        var images = ImmutableArray.CreateBuilder<ImageAsset>(imageDocuments.Count);
        foreach (var imageDocument in imageDocuments)
        {
            images.Add(MapImage(
                imageDocument ?? throw new ArgumentException("Коллекция изображений содержит null.")));
        }

        return images.MoveToImmutable();
    }

    private static NovelProject CreateProject(
        Guid id,
        string? name,
        ImmutableArray<NovelScene> scenes,
        ImmutableArray<ImageAsset> images,
        ImmutableArray<NovelCharacter> characters,
        string? defaultLanguage,
        DialogueUiDocument? dialogueUi)
    {
        if (dialogueUi is null)
        {
            throw new ArgumentException("Настройки интерфейса диалога отсутствуют.");
        }

        return new NovelProject(NovelProject.CurrentFormatVersion, id, name!)
        {
            Scenes = scenes,
            Images = images,
            Characters = characters,
            DefaultLanguage = defaultLanguage!,
            DialogueUi = new DialogueUiSettings
            {
                Css = dialogueUi.Css!,
                ShowVoiceProgress = dialogueUi.ShowVoiceProgress,
                ShowVoiceControls = dialogueUi.ShowVoiceControls,
                ShowNavigationControls = dialogueUi.ShowNavigationControls,
            },
        };
    }

    private static ImmutableArray<NovelScene> MapScenes<TSceneDocument>(
        List<TSceneDocument?>? sceneDocuments,
        Func<TSceneDocument, NovelScene> mapScene)
        where TSceneDocument : class
    {
        if (sceneDocuments is null)
        {
            throw new ArgumentException("Коллекция сцен проекта отсутствует.");
        }

        var scenes = ImmutableArray.CreateBuilder<NovelScene>(sceneDocuments.Count);
        foreach (var sceneDocument in sceneDocuments)
        {
            scenes.Add(mapScene(sceneDocument ?? throw new ArgumentException("Коллекция сцен содержит null.")));
        }

        return scenes.MoveToImmutable();
    }

    private static NovelScene MapVersion2Scene(Version2SceneDocument document) =>
        CreateScene(document.Id, document.Name, document.Lines, MapLegacyLine, document.DialogueCssOverride, null);

    private static NovelScene MapVersion3Scene(Version3SceneDocument document) =>
        CreateScene(
            document.Id,
            document.Name,
            document.Lines,
            MapLegacyLine,
            document.DialogueCssOverride,
            document.BackgroundImageId);

    private static NovelScene MapVersion4Scene(Version4SceneDocument document) =>
        CreateScene(
            document.Id,
            document.Name,
            document.Lines,
            MapVersion4Line,
            document.DialogueCssOverride,
            document.BackgroundImageId);

    private static NovelScene MapVersion5Scene(Version5SceneDocument document) =>
        CreateScene(
            document.Id,
            document.Name,
            document.Lines,
            MapVersion5Line,
            document.DialogueCssOverride,
            document.BackgroundImageId);

    private static NovelScene CreateScene<TLineDocument>(
        Guid id,
        string? name,
        List<TLineDocument?>? lineDocuments,
        Func<TLineDocument, DialogueLine> mapLine,
        string? dialogueCssOverride,
        Guid? backgroundImageId)
        where TLineDocument : class
    {
        if (lineDocuments is null)
        {
            throw new ArgumentException("Коллекция реплик сцены отсутствует.");
        }

        var lines = ImmutableArray.CreateBuilder<DialogueLine>(lineDocuments.Count);
        foreach (var lineDocument in lineDocuments)
        {
            lines.Add(mapLine(lineDocument ?? throw new ArgumentException("Коллекция реплик содержит null.")));
        }

        return new NovelScene(id, name!)
        {
            Lines = lines.MoveToImmutable(),
            DialogueCssOverride = dialogueCssOverride,
            BackgroundImageId = backgroundImageId,
        };
    }

    private static DialogueLine MapLegacyLine(LegacyLineDocument document) =>
        CreateLine(document.Id, document.TextKey, document.Texts, null, null, null);

    private static DialogueLine MapVersion4Line(Version4LineDocument document) =>
        CreateLine(
            document.Id,
            document.TextKey,
            document.Texts,
            document.SpeakerId,
            document.CharacterStateId,
            document.HideCharacterId);

    private static DialogueLine MapVersion5Line(Version5LineDocument document) =>
        CreateLine(document.Id, document.TextKey, document.Texts, document.SpeakerId, document.CharacterStateId, document.HideCharacterId) with
        {
            Placements = document.Placements?.Select(command => command is null
                ? throw new ArgumentException("Команда размещения равна null.")
                : new CharacterPlacementCommand(command.CharacterId, command.Placement is null ? null :
                    new CharacterPlacement(command.Placement.X, command.Placement.Y, command.Placement.Scale, command.Placement.Layer))).ToImmutableArray()
                ?? throw new ArgumentException("Коллекция размещений отсутствует."),
        };

    private static DialogueLine CreateLine(
        Guid id,
        string? textKey,
        SortedDictionary<string, string?>? textDocuments,
        Guid? speakerId,
        Guid? characterStateId,
        Guid? hideCharacterId)
    {
        if (textDocuments is null)
        {
            throw new ArgumentException("Словарь переводов реплики отсутствует.");
        }

        var texts = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (language, text) in textDocuments)
        {
            texts.Add(language, text!);
        }

        return new DialogueLine(id, textKey!)
        {
            Texts = texts.ToImmutable(),
            SpeakerId = speakerId,
            CharacterStateId = characterStateId,
            HideCharacterId = hideCharacterId,
        };
    }

    private static ImageAsset MapImage(ImageDocument document) => new(
        document.Id,
        document.Name!,
        document.RelativePath!,
        document.PixelWidth,
        document.PixelHeight);

    private static NovelCharacter MapCharacter(CharacterDocument document)
    {
        if (document.States is null)
        {
            throw new ArgumentException("Коллекция состояний персонажа отсутствует.");
        }

        var states = ImmutableArray.CreateBuilder<CharacterState>(document.States.Count);
        foreach (var stateDocument in document.States)
        {
            var state = stateDocument ?? throw new ArgumentException("Коллекция состояний персонажа содержит null.");
            states.Add(new CharacterState(state.Id, state.Name!, state.ImageId));
        }

        return new NovelCharacter(document.Id, document.Name!)
        {
            States = states.MoveToImmutable(),
            DefaultStateId = document.DefaultStateId,
        };
    }

}
