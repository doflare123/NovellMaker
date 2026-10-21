using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Projects;

/// <summary>
/// Root metadata for an editable visual novel project.
/// </summary>
public sealed record NovelProject(int FormatVersion, Guid Id, string Name)
{
    public const int CurrentFormatVersion = 15;

    public int FormatVersion { get; init; } = ValidateFormatVersion(FormatVersion);

    public Guid Id { get; init; } = ValidateId(Id);

    public string Name { get; init; } = ValidateName(Name);

    public NovellMaker.Core.Audio.AudioProject Audio { get; init; } = new();

    public ImmutableArray<NovelScene> Scenes { get; init; } = [];

    public ImmutableArray<ImageAsset> Images { get; init; } = [];

    public ImmutableArray<NovelCharacter> Characters { get; init; } = [];

    public ImmutableArray<NovellMaker.Core.Execution.LuaScriptAsset> Scripts { get; init; } = [];

    // Adapter for the screen host and older authoring operations. Scenes are the only stored resources.
    [System.Text.Json.Serialization.JsonIgnore]
    public ImmutableArray<NovellMaker.Core.Screens.ScreenAsset> Screens
    {
        get => Scenes.Select(s => s.AsScreen()).ToImmutableArray();
    }

    public NovelProject WithCanvas(NovellMaker.Core.Screens.ScreenAsset canvas)
    {
        var index = -1;
        for (var i = 0; i < Scenes.Length; i++) if (Scenes[i].Id == canvas.Id) { index = i; break; }
        if (index < 0) throw new ArgumentException("Сцена отсутствует.");
        return this with { Scenes = Scenes.SetItem(index, Scenes[index] with { Name = canvas.Name, Canvas = SceneCanvas.FromScreen(canvas) }) };
    }

    public ImmutableArray<NovellMaker.Core.Screens.ProjectInputBinding> InputBindings { get; init; } = [];

    public string DefaultLanguage { get; init; } = "ru";

    public bool ExplorerShowsFiles { get; init; }

    public DialogueUiSettings DialogueUi { get; init; } = new();

    public static NovelProject Create(string name) =>
        new(CurrentFormatVersion, Guid.NewGuid(), name);

    internal static void ValidateForPersistence(NovelProject project)
    {
        ValidateFormatVersion(project.FormatVersion);
        ValidateId(project.Id);
        ValidateName(project.Name);
        ValidateGraph(project);
        NovellMaker.Core.Audio.AudioProject.Validate(project);
        NovellMaker.Core.Execution.ScenarioProgram.Validate(project);
        NovellMaker.Core.Screens.ScreenAsset.ValidateProject(project);
    }

    public bool ContentEquals(NovelProject? other)
    {
        if (other is null ||
            FormatVersion != other.FormatVersion ||
            !NovellMaker.Core.Audio.AudioContent.Equals(Audio, other.Audio) ||
            Id != other.Id ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
            !string.Equals(DefaultLanguage, other.DefaultLanguage, StringComparison.Ordinal) ||
            ExplorerShowsFiles != other.ExplorerShowsFiles ||
            !DialogueUiEquals(DialogueUi, other.DialogueUi) ||
            Scenes.IsDefault != other.Scenes.IsDefault ||
            Images.IsDefault != other.Images.IsDefault ||
            Characters.IsDefault != other.Characters.IsDefault)
        {
            return false;
        }

        if (InputBindings.IsDefault != other.InputBindings.IsDefault || !InputBindings.IsDefault && !InputBindings.SequenceEqual(other.InputBindings)) return false;

        if (Scripts.IsDefault != other.Scripts.IsDefault || !Scripts.IsDefault &&
            (Scripts.Length != other.Scripts.Length || Scripts.Where((script, index) => !script.ContentEquals(other.Scripts[index])).Any())) return false;

        if (!Characters.IsDefault)
        {
            if (Characters.Length != other.Characters.Length)
            {
                return false;
            }

            for (var characterIndex = 0; characterIndex < Characters.Length; characterIndex++)
            {
                if (Characters[characterIndex] is null ||
                    !Characters[characterIndex].ContentEquals(other.Characters[characterIndex]))
                {
                    return false;
                }
            }
        }

        if (!Images.IsDefault)
        {
            if (Images.Length != other.Images.Length)
            {
                return false;
            }

            for (var imageIndex = 0; imageIndex < Images.Length; imageIndex++)
            {
                if (!ImageContentEquals(Images[imageIndex], other.Images[imageIndex]))
                {
                    return false;
                }
            }
        }

        if (!Scenes.IsDefault)
        {
            if (Scenes.Length != other.Scenes.Length)
            {
                return false;
            }

            for (var sceneIndex = 0; sceneIndex < Scenes.Length; sceneIndex++)
            {
                if (Scenes[sceneIndex] is null || !Scenes[sceneIndex].ContentEquals(other.Scenes[sceneIndex]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static bool ContentEquals(NovelProject? left, NovelProject? right) =>
        left is null ? right is null : left.ContentEquals(right);

    private static int ValidateFormatVersion(int formatVersion)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatVersion),
                formatVersion,
                $"Поддерживается только версия формата проекта {CurrentFormatVersion}.");
        }

        return formatVersion;
    }

    private static Guid ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор проекта не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Название проекта не может быть пустым.", nameof(name));
        }

        return name;
    }

    private static void ValidateGraph(NovelProject project)
    {
        if (project.Scenes.IsDefault)
        {
            throw new ArgumentException("Коллекция сцен проекта не инициализирована.", nameof(project));
        }

        if (project.Images.IsDefault)
        {
            throw new ArgumentException("Коллекция изображений проекта не инициализирована.", nameof(project));
        }

        if (project.Characters.IsDefault)
        {
            throw new ArgumentException("Коллекция персонажей проекта не инициализирована.", nameof(project));
        }

        if (string.IsNullOrWhiteSpace(project.DefaultLanguage))
        {
            throw new ArgumentException("Язык проекта по умолчанию не может быть пустым.", nameof(project));
        }

        if (project.DialogueUi is null)
        {
            throw new ArgumentException("Настройки интерфейса диалога отсутствуют.", nameof(project));
        }

        if (project.DialogueUi.Css is null)
        {
            throw new ArgumentException("CSS интерфейса диалога не может быть null.", nameof(project));
        }

        var ids = new HashSet<Guid> { project.Id };
        if (project.Scripts.IsDefault) throw new ArgumentException("Коллекция скриптов отсутствует.");
        foreach (var script in project.Scripts)
        {
            if (script is null) throw new ArgumentException("Коллекция скриптов содержит null.");
            script.Validate();
            if (!ids.Add(script.Id)) throw new ArgumentException($"Идентификатор {script.Id} используется более одного раза.");
        }
        var textKeys = new HashSet<string>(StringComparer.Ordinal);
        var imagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var image in project.Images)
        {
            if (image is null)
            {
                throw new ArgumentException("Коллекция изображений содержит null.", nameof(project));
            }

            ImageAsset.ValidateForPersistence(image);
            if (!ids.Add(image.Id))
            {
                throw new ArgumentException($"Идентификатор {image.Id} используется более одного раза.", nameof(project));
            }

            if (!imagePaths.Add(image.RelativePath))
            {
                throw new ArgumentException(
                    $"Путь изображения «{image.RelativePath}» используется более одного раза.",
                    nameof(project));
            }
        }

        foreach (var character in project.Characters)
        {
            if (character is null)
            {
                throw new ArgumentException("Коллекция персонажей содержит null.", nameof(project));
            }

            NovelCharacter.ValidateForPersistence(character);
            if (!ids.Add(character.Id))
            {
                throw new ArgumentException($"Идентификатор {character.Id} используется более одного раза.", nameof(project));
            }

            foreach (var state in character.States)
            {
                if (!ids.Add(state.Id))
                {
                    throw new ArgumentException($"Идентификатор {state.Id} используется более одного раза.", nameof(project));
                }
            }
        }

        foreach (var scene in project.Scenes)
        {
            if (scene is null)
            {
                throw new ArgumentException("Коллекция сцен содержит null.", nameof(project));
            }

            if (scene.Id == Guid.Empty)
            {
                throw new ArgumentException("Идентификатор сцены не может быть пустым.", nameof(project));
            }

            if (scene.Canvas is null) throw new ArgumentException("Компоненты сцены отсутствуют.");

            if (!ids.Add(scene.Id))
            {
                throw new ArgumentException($"Идентификатор {scene.Id} используется более одного раза.", nameof(project));
            }

            if (string.IsNullOrWhiteSpace(scene.Name))
            {
                throw new ArgumentException("Название сцены не может быть пустым.", nameof(project));
            }

            if (scene.BackgroundImageId == Guid.Empty)
            {
                throw new ArgumentException("Ссылка на фоновое изображение не может быть пустым GUID.", nameof(project));
            }

            if (scene.Lines.IsDefault)
            {
                throw new ArgumentException($"Коллекция реплик сцены «{scene.Name}» не инициализирована.", nameof(project));
            }

            foreach (var line in scene.Lines)
            {
                if (line is null)
                {
                    throw new ArgumentException($"Сцена «{scene.Name}» содержит реплику null.", nameof(project));
                }

                if (line.Id == Guid.Empty)
                {
                    throw new ArgumentException("Идентификатор реплики не может быть пустым.", nameof(project));
                }

                if (line.Placements.IsDefault || line.Placements.Any(command => command is null ||
                    command.CharacterId == Guid.Empty || command.Placement is { IsValid: false }) ||
                    line.Placements.Select(command => command.CharacterId).Distinct().Count() != line.Placements.Length)
                    throw new ArgumentException("Некорректные команды размещения персонажей.", nameof(project));

                if (line.SpeakerId == Guid.Empty)
                {
                    throw new ArgumentException("Ссылка реплики на персонажа не может быть пустым GUID.", nameof(project));
                }

                if (line.CharacterStateId == Guid.Empty)
                {
                    throw new ArgumentException("Ссылка реплики на состояние персонажа не может быть пустым GUID.", nameof(project));
                }

                if (line.HideCharacterId == Guid.Empty)
                {
                    throw new ArgumentException("Ссылка реплики на скрываемого персонажа не может быть пустым GUID.", nameof(project));
                }

                if (!ids.Add(line.Id))
                {
                    throw new ArgumentException($"Идентификатор {line.Id} используется более одного раза.", nameof(project));
                }

                if (string.IsNullOrWhiteSpace(line.TextKey))
                {
                    throw new ArgumentException("Текстовый ключ реплики не может быть пустым.", nameof(project));
                }

                if (!textKeys.Add(line.TextKey))
                {
                    throw new ArgumentException($"Текстовый ключ «{line.TextKey}» используется более одного раза.", nameof(project));
                }

                if (line.Texts is null)
                {
                    throw new ArgumentException($"Переводы реплики «{line.TextKey}» не инициализированы.", nameof(project));
                }

                foreach (var (language, text) in line.Texts)
                {
                    if (string.IsNullOrWhiteSpace(language))
                    {
                        throw new ArgumentException($"Реплика «{line.TextKey}» содержит пустой код языка.", nameof(project));
                    }

                    if (text is null)
                    {
                        throw new ArgumentException($"Текст реплики «{line.TextKey}» для языка «{language}» равен null.", nameof(project));
                    }
                }
            }
        }
    }

    private static bool DialogueUiEquals(DialogueUiSettings? left, DialogueUiSettings? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.Css, right.Css, StringComparison.Ordinal) &&
        left.ShowVoiceProgress == right.ShowVoiceProgress &&
        left.ShowVoiceControls == right.ShowVoiceControls &&
        left.ShowNavigationControls == right.ShowNavigationControls;

    private static bool ImageContentEquals(ImageAsset? left, ImageAsset? right) =>
        left is not null &&
        right is not null &&
        left.Id == right.Id &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) &&
        left.PixelWidth == right.PixelWidth &&
        left.PixelHeight == right.PixelHeight;

}
