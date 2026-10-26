using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Execution;

public enum PlaybackMode { AuthorPreview, Game }

/// <summary>Presentation/navigation facade over the shared action interpreter.</summary>
public sealed class ScenePlayer : IDisposable, IAsyncDisposable
{
    private readonly NovelProject project;
    private readonly NovelScene scene;
    private readonly string language;
    private readonly PlaybackMode mode;
    private readonly Func<ScriptAction, GameState, NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> runLua;
    public ScenarioPlayer Execution { get; private set; }
    public bool UsesActions => scene.HasActionScript || project.Scenes.First(s => s.Id == Execution.State.SceneId).HasActionScript;
    public bool IsGamePlayback => mode == PlaybackMode.Game;
    private bool CanNavigate => !IsGamePlayback && !UsesActions && Execution.State.SceneId == scene.Id;
    public SceneFrame Frame { get; private set; } = null!;
    private GameState? presentedState;
    /// <summary>Refresh after a host-owned lifecycle completion; executes no commands and advances no time.</summary>
    public void SynchronizePresentation() { if (!ReferenceEquals(presentedState, Execution.State)) UpdateFrame(); }

    public ScenePlayer(NovelProject project, Guid sceneId, string language, Guid? startLineId = null, PlaybackMode mode = PlaybackMode.AuthorPreview, bool startImmediately = true)
        : this(project, sceneId, language, startLineId, mode, startImmediately, LuaActionScript.RequestCommandsAsync) { }

    internal ScenePlayer(NovelProject project, Guid sceneId, string language, Guid? startLineId, PlaybackMode mode, bool startImmediately,
        Func<ScriptAction, GameState, NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> runLua)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        this.project = project;
        this.language = language;
        this.mode = mode; this.runLua = runLua;
        if (mode == PlaybackMode.Game && startLineId is not null)
            throw new ArgumentException("Игра начинается со стартовой сцены; выбранная фраза относится к навигации автора.", nameof(startLineId));
        scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId)
            ?? throw new ArgumentException($"Сцена {sceneId} отсутствует в проекте.", nameof(sceneId));
        if (startLineId.HasValue && !scene.Lines.Any(l => l.Id == startLineId))
            throw new ArgumentException($"Реплика {startLineId} отсутствует в сцене.", nameof(startLineId));
        Execution = new(project, sceneId, language, runLua, startImmediately);
        // Arbitrary action scripts must not be replayed to seek to a dialogue line.
        if (startImmediately && CanNavigate && startLineId is { } line)
            while (Execution.State.LineId != line && Execution.Advance()) { }
        if (startImmediately) ResetNavigationAudio();
        UpdateFrame();
    }

    private void ResetNavigationAudio()
    {
        if (!CanNavigate) return;
        Execution.DrainAudioEvents();
        navigationAudio = [new(NovellMaker.Core.Audio.AudioEventKind.Scene, Execution.State.SceneId), new(NovellMaker.Core.Audio.AudioEventKind.Line, Execution.State.SceneId, Execution.State.LineId)];
    }
    private NovellMaker.Core.Audio.AudioEvent[] navigationAudio = [];
    public NovellMaker.Core.Audio.AudioEvent[] DrainAudioEvents()
    {
        var result = navigationAudio.Concat(Execution.DrainAudioEvents()).ToArray(); navigationAudio = []; return result;
    }

    /// <summary>Continues only a pending player-input wait; never seeks, skips a timer or loads a save.</summary>
    public bool Next()
    {
        if (!Frame.CanNext || !Execution.Advance()) return false;
        UpdateFrame();
        return true;
    }

    public bool Previous()
    {
        if (!CanNavigate || Frame.LineIndex <= 0) return false;
        return Seek(scene.Lines[Frame.LineIndex - 1].Id);
    }

    /// <summary>Author navigation for linear scenes only. Reconstructs cues; unavailable in game playback.</summary>
    public bool Seek(Guid lineId)
    {
        if (!CanNavigate || !scene.Lines.Any(l => l.Id == lineId)) return false;
        Execution.Dispose();
        Execution = new(project, scene.Id, language, runLua);
        while (Execution.State.LineId != lineId && Execution.Advance()) { }
        ResetNavigationAudio();
        UpdateFrame();
        return true;
    }

    public void Tick(TimeSpan elapsed) { Execution.Tick(elapsed); UpdateFrame(); }
    public bool PollLua() { var changed = Execution.PollLua(); if (changed) UpdateFrame(); return changed; }
    public void Dispose() => Execution.Dispose();
    public ValueTask DisposeAsync() => Execution.DisposeAsync();
    public bool Choose(Guid optionId) { var changed = Execution.Choose(optionId); if (changed) UpdateFrame(); return changed; }
    public bool Click(Guid objectId) { var changed = Execution.Click(objectId); if (changed) UpdateFrame(); return changed; }
    public bool EnterScene(Guid sceneId) { var changed = Execution.EnterScene(sceneId); if (changed) { navigationAudio = []; UpdateFrame(); } return changed; }
    /// <summary>Validates and replaces state atomically. Does not replay commands, advance time or run Lua.</summary>
    public void LoadSave(string json) { GameSave.Load(Execution, json); navigationAudio = []; UpdateFrame(); }

    private void UpdateFrame()
    {
        presentedState = Execution.State;
        var frame = Execution.Frame;
        Frame = frame with { CanPrevious = CanNavigate && frame.LineIndex > 0,
            CanNext = CanNavigate ? frame.LineIndex >= 0 && frame.LineIndex < scene.Lines.Length - 1 : frame.CanNext };
    }
}

public sealed record SceneFrame(Guid? LineId, int LineIndex, string Text, string SpeakerName, ImageAsset? Background,
    ImmutableArray<VisibleCharacter> Characters, DialogueStyle Style, ImmutableArray<string> Diagnostics,
    bool CanPrevious, bool CanNext)
{
    public ImmutableDictionary<Guid, CharacterPlacement> Placements { get; init; } = ImmutableDictionary<Guid, CharacterPlacement>.Empty;
}

public sealed record VisibleCharacter(Guid CharacterId, string Name, Guid StateId, ImageAsset Image, CharacterPlacement? Placement = null, int AutomaticSlot = 0);
