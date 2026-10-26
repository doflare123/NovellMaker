using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Core.Execution;

public sealed partial class ScenarioPlayer
{
    /// <summary>Host save barrier: checked before capture; never invoked during background serialization.</summary>
    public Func<string?>? MinigameSaveBarrier { get; set; }
    public bool MinigameRestored { get; private set; }

    public bool CommitMinigame(MinigameSnapshot snapshot)
    {
        if (disposed || State.Minigame is not { } current || current.RunId != snapshot.RunId ||
            current.ActionId != snapshot.ActionId || current.SceneId != snapshot.SceneId ||
            current.Parameters.ToJson() != snapshot.Parameters.ToJson()) return false;
        snapshot.Validate(project.Scenes.Single(s => s.Id == snapshot.SceneId).Canvas.Minigame!);
        State = State with { Minigame = snapshot }; return true;
    }

    public bool FinishMinigame(Guid runId, MinigameResult result)
    {
        if (disposed || State.Minigame is not { } active || active.RunId != runId || State.Wait?.Mode != "minigame") return false;
        result.Validate(project.Scenes.Single(s => s.Id == active.SceneId).Canvas.Minigame!);
        State = State with { Minigame = null, MinigameOutcome = new(active.SceneId, result), Wait = null };
        // Host overlays may still pause the scenario. The next unpaused Tick pumps normally.
        Pump(); return true;
    }

    private void StartMinigame(StartMinigameAction action)
    {
        MinigameRestored = false;
        var definition = project.Scenes.FirstOrDefault(s => s.Id == action.SceneId)?.Canvas.Minigame
            ?? throw new InvalidOperationException("Ресурс мини-игры отсутствует.");
        definition.Validate(); definition.ParametersSchema.Validate(action.Parameters);
        State = State with { Minigame = new(Guid.NewGuid(), action.Id, action.SceneId, action.Parameters, definition.InitialState, LuaRandomState.Initial),
            MinigameOutcome = null, Wait = new(action.Id, "minigame") };
    }

    private bool MatchesMinigame(BranchMinigameAction action)
    {
        if (State.MinigameOutcome is not { } outcome) return false;
        var value = action.Field == "$status" ? LuaValue.FromString(outcome.Result.Phase.ToString().ToLowerInvariant()) :
            outcome.Result.Data.Kind == LuaValueKind.Object ? outcome.Result.Data.Properties!.GetValueOrDefault(action.Field) : null;
        return value?.ToJson() == action.EqualsValue.ToJson();
    }
}
