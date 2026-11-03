using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

/// <summary>History access never executes story commands; replay belongs to the requesting canvas lifetime.</summary>
internal sealed class RuntimeHistoryServices(GameSession session, Func<Guid, string, bool> canReplay,
    Func<Guid, string, bool> replay, Action stop, Action<bool> pause)
{
    private ScreenInstance? replayOwner;
    private long replayGeneration;
    internal Func<ScreenInstance, bool> IsCurrentOrigin { get; set; } = _ => true;
    internal event Action<ScreenInstance?, GameServiceResult>? Completed;

    internal bool Request(ScreenInstance? origin, GameServiceCommand command, long generation)
    {
        command.Validate();
        if (command.Action is not (GameServiceAction.GetHistory or GameServiceAction.ReplayHistoryVoice))
            throw new ArgumentException("Ожидался запрос истории.");
        if (!session.IsCurrentGeneration(generation) || origin is null || !IsCurrentOrigin(origin)) return false;
        var execution = session.Player.Execution;
        HistoryPage? history = null;
        var success = true;
        var code = "ok";
        if (command.Action == GameServiceAction.GetHistory)
            history = HistoryPage.Read(execution.Project, execution.State, command.HistoryIndex!.Value,
                command.HistoryPage!.Value, command.HistoryPageSize!.Value, canReplay);
        else
        {
            var entry = execution.State.History.FirstOrDefault(e => e.VisitId == command.VisitId);
            if (session.Phase != GameSessionPhase.Playing || entry is null) { success = false; code = "history_unavailable"; }
            else if (!canReplay(entry.LineId, entry.Language) || !replay(entry.LineId, entry.Language)) { success = false; code = "voice_unavailable"; }
            else { replayOwner = origin; replayGeneration = generation; }
        }
        var result = new GameServiceResult(command.RequestId, command.Action, success, code, null, []) { History = history };
        result.Validate(); Completed?.Invoke(origin, result); return success;
    }

    internal void Update(bool active, ScreenInstance? top)
    {
        if (replayOwner is null) return;
        if (!session.IsCurrentGeneration(replayGeneration) || !IsCurrentOrigin(replayOwner))
        { stop(); replayOwner = null; return; }
        pause(!active || !ReferenceEquals(top, replayOwner));
    }
}
