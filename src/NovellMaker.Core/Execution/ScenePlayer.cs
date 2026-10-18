using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Execution;

/// <summary>Shared, deterministic interpreter for the currently supported scene commands.</summary>
public sealed class ScenePlayer
{
    private readonly NovelProject project;
    private readonly NovelScene scene;
    private readonly string language;
    private readonly Dictionary<Guid, NovelCharacter> characters;
    private readonly Dictionary<Guid, ImageAsset> images;
    private readonly Dictionary<Guid, Guid> states = [];
    private readonly Dictionary<Guid, CharacterPlacement> placements = [];
    private readonly List<Guid> visible = [];
    private readonly List<string> diagnostics = [];
    private readonly DialogueStyle style;
    private readonly string? styleError;
    private int index = -1;

    public ScenePlayer(NovelProject project, Guid sceneId, string language, Guid? startLineId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        this.project = project;
        this.language = language;
        scene = project.Scenes.FirstOrDefault(item => item.Id == sceneId)
            ?? throw new ArgumentException($"Сцена {sceneId} отсутствует в проекте.", nameof(sceneId));
        characters = project.Characters.ToDictionary(character => character.Id);
        images = project.Images.ToDictionary(image => image.Id);
        try { style = DialogueCssParser.Parse(scene.DialogueCssOverride ?? project.DialogueUi.Css); }
        catch (DialogueCssException error)
        {
            style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
            styleError = error.Message;
        }
        var target = startLineId is null ? 0 : FindLine(startLineId.Value);
        if (startLineId.HasValue && target < 0)
            throw new ArgumentException($"Реплика {startLineId} отсутствует в сцене.", nameof(startLineId));
        Replay(target);
    }

    public SceneFrame Frame { get; private set; } = null!;

    public bool Next()
    {
        if (index >= scene.Lines.Length - 1) return false;
        Apply(++index);
        UpdateFrame();
        return true;
    }

    public bool Previous()
    {
        if (index <= 0) return false;
        Replay(index - 1);
        return true;
    }

    public bool Seek(Guid lineId)
    {
        var target = FindLine(lineId);
        if (target < 0) return false;
        Replay(target);
        return true;
    }

    private int FindLine(Guid lineId)
    {
        for (var number = 0; number < scene.Lines.Length; number++)
            if (scene.Lines[number].Id == lineId) return number;
        return -1;
    }

    private void Replay(int target)
    {
        states.Clear();
        placements.Clear();
        visible.Clear();
        diagnostics.Clear();
        if (styleError is not null) diagnostics.Add($"CSS сцены: {styleError}");
        if (scene.BackgroundImageId is { } backgroundId && !images.ContainsKey(backgroundId))
            diagnostics.Add($"Задник {backgroundId} отсутствует в библиотеке.");
        index = -1;
        while (index < target && index < scene.Lines.Length - 1) Apply(++index);
        UpdateFrame();
    }

    private void Apply(int number)
    {
        var line = scene.Lines[number];
        var prefix = $"Реплика {number + 1} ({line.Id}): ";
        foreach (var command in line.Placements)
        {
            if (!characters.ContainsKey(command.CharacterId))
                diagnostics.Add(prefix + $"персонаж размещения {command.CharacterId} отсутствует.");
            else if (command.Placement is { IsValid: true } placement) placements[command.CharacterId] = placement;
            else if (command.Placement is null) placements.Remove(command.CharacterId);
            else diagnostics.Add(prefix + "некорректное размещение персонажа.");
        }
        if (line.HideCharacterId is { } hidden)
        {
            if (!characters.ContainsKey(hidden)) diagnostics.Add(prefix + $"скрываемый персонаж {hidden} отсутствует.");
            visible.Remove(hidden);
        }
        if (line.SpeakerId is not { } speakerId)
        {
            if (line.CharacterStateId.HasValue) diagnostics.Add(prefix + "состояние задано без говорящего персонажа.");
            return;
        }
        if (!characters.TryGetValue(speakerId, out var character))
        {
            diagnostics.Add(prefix + $"говорящий персонаж {speakerId} отсутствует.");
            return;
        }
        var stateId = line.CharacterStateId ?? (states.TryGetValue(speakerId, out var previous) ? previous : character.DefaultStateId);
        var pose = character.States.FirstOrDefault(item => item.Id == stateId);
        if (pose is null)
        {
            diagnostics.Add(prefix + $"у персонажа «{character.Name}» отсутствует состояние {stateId?.ToString() ?? "по умолчанию"}.");
            visible.Remove(speakerId);
            return;
        }
        states[speakerId] = pose.Id;
        if (!images.ContainsKey(pose.ImageId))
        {
            diagnostics.Add(prefix + $"изображение состояния «{character.Name}/{pose.Name}» ({pose.ImageId}) отсутствует.");
            visible.Remove(speakerId);
            return;
        }
        if (!visible.Contains(speakerId)) visible.Add(speakerId);
    }

    private void UpdateFrame()
    {
        var line = index >= 0 ? scene.Lines[index] : null;
        var text = "";
        var currentDiagnostics = diagnostics.ToImmutableArray().ToBuilder();
        if (line is not null && !line.Texts.TryGetValue(language, out text!))
        {
            text = line.Texts.GetValueOrDefault(project.DefaultLanguage, "");
            currentDiagnostics.Add($"Реплика {index + 1}: нет перевода «{language}», используется основной язык «{project.DefaultLanguage}».");
        }
        var speaker = line?.SpeakerId is { } id
            ? characters.GetValueOrDefault(id)?.Name ?? "Неизвестный персонаж" : "";
        var sprites = visible.Select((id, slotIndex) =>
        {
            var character = characters[id];
            var state = character.States.First(item => item.Id == states[id]);
            return new VisibleCharacter(id, character.Name, state.Id, images[state.ImageId], placements.GetValueOrDefault(id), slotIndex);
        }).OrderBy(sprite => sprite.Placement?.Layer ?? 0).ToImmutableArray();
        Frame = new(line?.Id, index, text, speaker,
            scene.BackgroundImageId is { } backgroundId ? images.GetValueOrDefault(backgroundId) : null,
            sprites, style, currentDiagnostics.ToImmutable(), index > 0, index >= 0 && index < scene.Lines.Length - 1)
        { Placements = placements.ToImmutableDictionary() };
    }
}

public sealed record SceneFrame(Guid? LineId, int LineIndex, string Text, string SpeakerName, ImageAsset? Background,
    ImmutableArray<VisibleCharacter> Characters, DialogueStyle Style, ImmutableArray<string> Diagnostics,
    bool CanPrevious, bool CanNext)
{
    public ImmutableDictionary<Guid, CharacterPlacement> Placements { get; init; } = ImmutableDictionary<Guid, CharacterPlacement>.Empty;
}

public sealed record VisibleCharacter(Guid CharacterId, string Name, Guid StateId, ImageAsset Image, CharacterPlacement? Placement = null, int AutomaticSlot = 0);
