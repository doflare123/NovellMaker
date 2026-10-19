using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Projects;

public sealed class JsonProjectStore : IProjectStore
{
    private static readonly JsonSerializerOptions StrictSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);

    private static readonly JsonSerializerOptions VersionProbeSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Skip);
    private static readonly JsonSerializerOptions Version7SerializerOptions = CreateVersion7Options();
    private static readonly JsonSerializerOptions PreVersion10SerializerOptions = CreatePreVersion10Options();

    private static JsonSerializerOptions CreatePreVersion10Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ScenarioAction) && info.PolymorphismOptions is { } polymorphism)
                foreach (var type in polymorphism.DerivedTypes.Where(t => t.DerivedType == typeof(RunScriptAction)).ToArray()) polymorphism.DerivedTypes.Remove(type);
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    private static JsonSerializerOptions CreateVersion7Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ScriptAction))
                foreach (var property in info.Properties.Where(p => p.Name == "references").ToArray()) info.Properties.Remove(property);
            if (info.Type == typeof(ScenarioAction) && info.PolymorphismOptions is { } polymorphism)
                foreach (var type in polymorphism.DerivedTypes.Where(t => t.DerivedType == typeof(LineEntryAction) || t.DerivedType == typeof(RunScriptAction)).ToArray()) polymorphism.DerivedTypes.Remove(type);
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    public async Task<NovelProject> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        NovelProject project;

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var versionProbe = await JsonSerializer.DeserializeAsync<VersionProbe>(
                stream,
                VersionProbeSerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (versionProbe is null)
            {
                throw new ProjectFormatException("Файл проекта не содержит объекта проекта.");
            }

            if (versionProbe.FormatVersion > NovelProject.CurrentFormatVersion)
            {
                throw new ProjectFormatException(
                    $"Версия формата проекта {versionProbe.FormatVersion} не поддерживается редактором. " +
                    $"Поддерживается версия {NovelProject.CurrentFormatVersion}.");
            }

            stream.Position = 0;
            project = versionProbe.FormatVersion switch
            {
                1 => MigrateFromVersion1(await DeserializeRequiredAsync<Version1ProjectDocument>(
                    stream,
                    cancellationToken).ConfigureAwait(false)),
                2 => MigrateFromVersion2(await DeserializeRequiredAsync<Version2ProjectDocument>(
                    stream,
                    cancellationToken).ConfigureAwait(false)),
                3 => MigrateFromVersion3(await DeserializeRequiredAsync<Version3ProjectDocument>(
                    stream,
                    cancellationToken).ConfigureAwait(false)),
                4 => MapVersion4(await DeserializeRequiredAsync<Version4ProjectDocument>(
                    stream,
                    cancellationToken).ConfigureAwait(false)),
                5 => MapVersion5(await DeserializeRequiredAsync<Version5ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
                6 => MapVersion6(await DeserializeRequiredAsync<Version6ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
                7 => MapVersion7(await DeserializeRequiredAsync<Version7ProjectDocument>(stream, cancellationToken, Version7SerializerOptions).ConfigureAwait(false)),
                9 => MapVersion9(await DeserializeRequiredAsync<Version9ProjectDocument>(stream, cancellationToken, PreVersion10SerializerOptions).ConfigureAwait(false)),
                10 => MapVersion10(await DeserializeRequiredAsync<Version10ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
                8 => MapVersion8(await DeserializeRequiredAsync<Version8ProjectDocument>(stream, cancellationToken, PreVersion10SerializerOptions).ConfigureAwait(false)),
                _ => throw new ProjectFormatException(
                    $"Некорректная версия формата проекта: {versionProbe.FormatVersion}."),
            };
        }
        catch (ProjectFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException(
                "Файл проекта содержит некорректный JSON или не соответствует ожидаемой структуре.",
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ProjectFormatException(
                "Файл проекта использует неподдерживаемую структуру данных.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException(
                $"Файл проекта содержит некорректные данные: {exception.Message}",
                exception);
        }

        try
        {
            NovelProject.ValidateForPersistence(project);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException(
                $"Файл проекта содержит некорректные данные: {exception.Message}",
                exception);
        }

        return project;
    }

    public async Task SaveAsync(
        string path,
        NovelProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        NovelProject.ValidateForPersistence(project);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Не удалось определить каталог файла проекта.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var document = MapForSave(project);

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    StrictSerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions(
        JsonUnmappedMemberHandling unmappedMemberHandling) => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = unmappedMemberHandling,
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true,
        };

    private static async Task<TDocument> DeserializeRequiredAsync<TDocument>(
        Stream stream,
        CancellationToken cancellationToken,
        JsonSerializerOptions? options = null)
        where TDocument : class
    {
        var document = await JsonSerializer.DeserializeAsync<TDocument>(
            stream,
            options ?? StrictSerializerOptions,
            cancellationToken).ConfigureAwait(false);

        return document ?? throw new ProjectFormatException("Файл проекта не содержит объекта проекта.");
    }

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

    private static Version10ProjectDocument MapForSave(NovelProject project) => new()
    {
        Scripts = project.Scripts,
        Audio = project.Audio,
        ExplorerShowsFiles = project.ExplorerShowsFiles,
        FormatVersion = project.FormatVersion,
        Id = project.Id,
        Name = project.Name,
        Scenes = project.Scenes.Select(scene => (Version8SceneDocument?)MapSceneForSave(scene)).ToList(),
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

    private static Version8SceneDocument MapSceneForSave(NovelScene scene) => new()
    {
        Id = scene.Id,
        Name = scene.Name,
        Lines = scene.Lines.Select(line => (Version5LineDocument?)MapLineForSave(line)).ToList(),
        DialogueCssOverride = scene.DialogueCssOverride,
        BackgroundImageId = scene.BackgroundImageId,
        Actions = scene.Actions,
        UseActions = scene.UseActions,
        ScriptBlocks = scene.ScriptBlocks,
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

    private sealed class VersionProbe
    {
        [JsonRequired]
        public int FormatVersion { get; init; }
    }

    private sealed class Version1ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }
    }

    private sealed class Version2ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version2SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version3ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version3SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public List<ImageDocument?>? Images { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version4ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version4SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public List<ImageDocument?>? Images { get; init; }

        [JsonRequired]
        public List<CharacterDocument?>? Characters { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version6ProjectDocument : Version5ProjectDocument
    {
        [JsonRequired]
        public bool ExplorerShowsFiles { get; init; }
    }

    private sealed class Version7ProjectDocument
    {
        [JsonRequired] public int FormatVersion { get; init; }
        [JsonRequired] public Guid Id { get; init; }
        [JsonRequired] public string? Name { get; init; }
        [JsonRequired] public List<Version7SceneDocument?>? Scenes { get; init; }
        [JsonRequired] public List<ImageDocument?>? Images { get; init; }
        [JsonRequired] public List<CharacterDocument?>? Characters { get; init; }
        [JsonRequired] public string? DefaultLanguage { get; init; }
        [JsonRequired] public bool ExplorerShowsFiles { get; init; }
        [JsonRequired] public DialogueUiDocument? DialogueUi { get; init; }
    }

    private class Version7SceneDocument : Version5SceneDocument
    {
        [JsonRequired] public ImmutableArray<ScenarioAction>? Actions { get; init; }
        [JsonRequired] public bool UseActions { get; init; }
    }

    private sealed class Version8SceneDocument : Version7SceneDocument
    {
        [JsonRequired] public ImmutableArray<ScriptBlock>? ScriptBlocks { get; init; }
    }

    private class Version8ProjectDocument
    {
        [JsonRequired] public int FormatVersion { get; init; }
        [JsonRequired] public Guid Id { get; init; }
        [JsonRequired] public string? Name { get; init; }
        [JsonRequired] public List<Version8SceneDocument?>? Scenes { get; init; }
        [JsonRequired] public List<ImageDocument?>? Images { get; init; }
        [JsonRequired] public List<CharacterDocument?>? Characters { get; init; }
        [JsonRequired] public string? DefaultLanguage { get; init; }
        [JsonRequired] public bool ExplorerShowsFiles { get; init; }
        [JsonRequired] public DialogueUiDocument? DialogueUi { get; init; }
    }

    private class Version9ProjectDocument : Version8ProjectDocument
    {
        [JsonRequired] public NovellMaker.Core.Audio.AudioProject? Audio { get; init; }
    }

    private sealed class Version10ProjectDocument : Version9ProjectDocument
    {
        [JsonRequired] public ImmutableArray<LuaScriptAsset>? Scripts { get; init; }
    }

    private class Version5ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version5SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public List<ImageDocument?>? Images { get; init; }

        [JsonRequired]
        public List<CharacterDocument?>? Characters { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version2SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<LegacyLineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }
    }

    private sealed class Version3SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<LegacyLineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private sealed class Version4SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version4LineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private class Version5SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version5LineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private sealed class LegacyLineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }
    }

    private sealed class Version4LineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }

        [JsonRequired]
        public Guid? SpeakerId { get; init; }

        [JsonRequired]
        public Guid? CharacterStateId { get; init; }

        [JsonRequired]
        public Guid? HideCharacterId { get; init; }
    }

    private sealed class Version5LineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }

        [JsonRequired]
        public Guid? SpeakerId { get; init; }

        [JsonRequired]
        public Guid? CharacterStateId { get; init; }

        [JsonRequired]
        public Guid? HideCharacterId { get; init; }

        [JsonRequired]
        public List<PlacementCommandDocument?>? Placements { get; init; }
    }

    private sealed class PlacementCommandDocument
    {
        [JsonRequired] public Guid CharacterId { get; init; }
        [JsonRequired] public PlacementDocument? Placement { get; init; }
    }

    private sealed class PlacementDocument
    {
        [JsonRequired] public double X { get; init; }
        [JsonRequired] public double Y { get; init; }
        [JsonRequired] public double Scale { get; init; }
        [JsonRequired] public int Layer { get; init; }
    }

    private sealed class ImageDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public string? RelativePath { get; init; }

        [JsonRequired]
        public int PixelWidth { get; init; }

        [JsonRequired]
        public int PixelHeight { get; init; }
    }

    private sealed class CharacterDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<CharacterStateDocument?>? States { get; init; }

        [JsonRequired]
        public Guid? DefaultStateId { get; init; }
    }

    private sealed class CharacterStateDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public Guid ImageId { get; init; }
    }

    private sealed class DialogueUiDocument
    {
        [JsonRequired]
        public string? Css { get; init; }

        [JsonRequired]
        public bool ShowVoiceProgress { get; init; }

        [JsonRequired]
        public bool ShowVoiceControls { get; init; }

        [JsonRequired]
        public bool ShowNavigationControls { get; init; }
    }
}
