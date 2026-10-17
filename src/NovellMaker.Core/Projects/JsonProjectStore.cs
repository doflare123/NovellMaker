using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Projects;

public sealed class JsonProjectStore : IProjectStore
{
    private static readonly JsonSerializerOptions StrictSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);

    private static readonly JsonSerializerOptions VersionProbeSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Skip);

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
                NovelProject.CurrentFormatVersion => MapVersion3(await DeserializeRequiredAsync<Version3ProjectDocument>(
                    stream,
                    cancellationToken).ConfigureAwait(false)),
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
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true,
        };

    private static async Task<TDocument> DeserializeRequiredAsync<TDocument>(
        Stream stream,
        CancellationToken cancellationToken)
        where TDocument : class
    {
        var document = await JsonSerializer.DeserializeAsync<TDocument>(
            stream,
            StrictSerializerOptions,
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
            document.DefaultLanguage,
            document.DialogueUi);
    }

    private static NovelProject MapVersion3(Version3ProjectDocument document)
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
            document.DefaultLanguage,
            document.DialogueUi);
    }

    private static NovelProject CreateProject(
        Guid id,
        string? name,
        ImmutableArray<NovelScene> scenes,
        ImmutableArray<ImageAsset> images,
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
        CreateScene(document.Id, document.Name, document.Lines, document.DialogueCssOverride, null);

    private static NovelScene MapVersion3Scene(Version3SceneDocument document) =>
        CreateScene(
            document.Id,
            document.Name,
            document.Lines,
            document.DialogueCssOverride,
            document.BackgroundImageId);

    private static NovelScene CreateScene(
        Guid id,
        string? name,
        List<LineDocument?>? lineDocuments,
        string? dialogueCssOverride,
        Guid? backgroundImageId)
    {
        if (lineDocuments is null)
        {
            throw new ArgumentException("Коллекция реплик сцены отсутствует.");
        }

        var lines = ImmutableArray.CreateBuilder<DialogueLine>(lineDocuments.Count);
        foreach (var lineDocument in lineDocuments)
        {
            lines.Add(MapLine(lineDocument ?? throw new ArgumentException("Коллекция реплик содержит null.")));
        }

        return new NovelScene(id, name!)
        {
            Lines = lines.MoveToImmutable(),
            DialogueCssOverride = dialogueCssOverride,
            BackgroundImageId = backgroundImageId,
        };
    }

    private static DialogueLine MapLine(LineDocument document)
    {
        if (document.Texts is null)
        {
            throw new ArgumentException("Словарь переводов реплики отсутствует.");
        }

        var texts = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (language, text) in document.Texts)
        {
            texts.Add(language, text!);
        }

        return new DialogueLine(document.Id, document.TextKey!)
        {
            Texts = texts.ToImmutable(),
        };
    }

    private static ImageAsset MapImage(ImageDocument document) => new(
        document.Id,
        document.Name!,
        document.RelativePath!,
        document.PixelWidth,
        document.PixelHeight);

    private static Version3ProjectDocument MapForSave(NovelProject project) => new()
    {
        FormatVersion = project.FormatVersion,
        Id = project.Id,
        Name = project.Name,
        Scenes = project.Scenes.Select(scene => (Version3SceneDocument?)MapSceneForSave(scene)).ToList(),
        Images = project.Images.Select(image => (ImageDocument?)MapImageForSave(image)).ToList(),
        DefaultLanguage = project.DefaultLanguage,
        DialogueUi = new DialogueUiDocument
        {
            Css = project.DialogueUi.Css,
            ShowVoiceProgress = project.DialogueUi.ShowVoiceProgress,
            ShowVoiceControls = project.DialogueUi.ShowVoiceControls,
            ShowNavigationControls = project.DialogueUi.ShowNavigationControls,
        },
    };

    private static Version3SceneDocument MapSceneForSave(NovelScene scene) => new()
    {
        Id = scene.Id,
        Name = scene.Name,
        Lines = scene.Lines.Select(line => (LineDocument?)MapLineForSave(line)).ToList(),
        DialogueCssOverride = scene.DialogueCssOverride,
        BackgroundImageId = scene.BackgroundImageId,
    };

    private static LineDocument MapLineForSave(DialogueLine line)
    {
        var texts = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (language, text) in line.Texts)
        {
            texts.Add(language, text);
        }

        return new LineDocument
        {
            Id = line.Id,
            TextKey = line.TextKey,
            Texts = texts,
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

    private sealed class Version2SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<LineDocument?>? Lines { get; init; }

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
        public List<LineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private sealed class LineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }
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
