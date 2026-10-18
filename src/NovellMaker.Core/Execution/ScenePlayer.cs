using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Execution;

/// <summary>Presentation/navigation facade over the shared action interpreter.</summary>
public sealed class ScenePlayer
{
    private readonly NovelProject project;
    private readonly NovelScene scene;
    private readonly string language;
    public ScenarioPlayer Execution { get; private set; }
    public bool UsesActions => scene.HasActionScript;
    public SceneFrame Frame { get; private set; } = null!;

    public ScenePlayer(NovelProject project, Guid sceneId, string language, Guid? startLineId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        this.project = project;
        this.language = language;
        scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId)
            ?? throw new ArgumentException($"Сцена {sceneId} отсутствует в проекте.", nameof(sceneId));
        if (startLineId.HasValue && !scene.Lines.Any(l => l.Id == startLineId))
            throw new ArgumentException($"Реплика {startLineId} отсутствует в сцене.", nameof(startLineId));
        Execution = new(project, sceneId, language);
        // Arbitrary action scripts must not be replayed to seek to a dialogue line.
        if (!UsesActions && startLineId is { } line)
            while (Execution.State.LineId != line && Execution.Advance()) { }
        UpdateFrame();
    }

    public bool Next()
    {
        if (!Frame.CanNext || !Execution.Advance()) return false;
        UpdateFrame();
        return true;
    }

    public bool Previous()
    {
        if (UsesActions || Frame.LineIndex <= 0) return false;
        return Seek(scene.Lines[Frame.LineIndex - 1].Id);
    }

    public bool Seek(Guid lineId)
    {
        if (UsesActions || !scene.Lines.Any(l => l.Id == lineId)) return false;
        Execution = new(project, scene.Id, language);
        while (Execution.State.LineId != lineId && Execution.Advance()) { }
        UpdateFrame();
        return true;
    }

    public void Tick(TimeSpan elapsed) { Execution.Tick(elapsed); UpdateFrame(); }
    public bool Choose(Guid optionId) { var changed = Execution.Choose(optionId); if (changed) UpdateFrame(); return changed; }
    public bool Click(Guid objectId) { var changed = Execution.Click(objectId); if (changed) UpdateFrame(); return changed; }
    public void LoadSave(string json) { GameSave.Load(Execution, json); UpdateFrame(); }

    private void UpdateFrame()
    {
        var frame = Execution.Frame;
        Frame = frame with { CanPrevious = !UsesActions && frame.LineIndex > 0,
            CanNext = UsesActions ? frame.CanNext : frame.LineIndex >= 0 && frame.LineIndex < scene.Lines.Length - 1 };
    }
}

public sealed record SceneFrame(Guid? LineId, int LineIndex, string Text, string SpeakerName, ImageAsset? Background,
    ImmutableArray<VisibleCharacter> Characters, DialogueStyle Style, ImmutableArray<string> Diagnostics,
    bool CanPrevious, bool CanNext)
{
    public ImmutableDictionary<Guid, CharacterPlacement> Placements { get; init; } = ImmutableDictionary<Guid, CharacterPlacement>.Empty;
}

public sealed record VisibleCharacter(Guid CharacterId, string Name, Guid StateId, ImageAsset Image, CharacterPlacement? Placement = null, int AutomaticSlot = 0);
