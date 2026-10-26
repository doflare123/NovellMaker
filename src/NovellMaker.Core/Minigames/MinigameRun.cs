using System.Text.Json.Serialization;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Core.Minigames;

public enum MinigamePhase { Starting, Running, Paused, Completed, Cancelled, Failed, Disposed }
public sealed record MinigameResult([property: JsonRequired] MinigamePhase Phase,
    [property: JsonRequired] LuaValue Data, [property: JsonRequired] string Message)
{
    public void Validate(MinigameDefinition definition)
    {
        if (Phase is not (MinigamePhase.Completed or MinigamePhase.Cancelled or MinigamePhase.Failed) ||
            Message is null || Message.Length > 1024 || Data is null)
            throw new ArgumentException("Некорректный результат мини-игры.");
        if (Phase == MinigamePhase.Completed) definition.ResultSchema.Validate(Data);
        else if (Data.Kind != LuaValueKind.Null) throw new ArgumentException("Отмена/ошибка не содержит результата.");
    }
}
public sealed record MinigameSnapshot([property: JsonRequired] Guid RunId, [property: JsonRequired] Guid ActionId,
    [property: JsonRequired] Guid SceneId, [property: JsonRequired] LuaValue Parameters,
    [property: JsonRequired] LuaValue State, [property: JsonRequired] LuaRandomState RandomState)
{
    [JsonRequired] public bool Initialized { get; init; }
    public void Validate(MinigameDefinition definition)
    {
        if (RunId == Guid.Empty || ActionId == Guid.Empty || SceneId == Guid.Empty || RandomState is null)
            throw new ArgumentException("Некорректный снимок мини-игры.");
        definition.ParametersSchema.Validate(Parameters); definition.StateSchema.Validate(State);
    }
}
public sealed record MinigameOutcome([property: JsonRequired] Guid SceneId, [property: JsonRequired] MinigameResult Result);

/// <summary>Host-owned lifecycle. Cleanup executes exactly once, including failure/cancellation/disposal.</summary>
public sealed class MinigameRun : IDisposable
{
    private readonly MinigameDefinition definition;
    private Action? cleanup;
    public MinigameRun(MinigameDefinition definition, MinigameSnapshot snapshot, Action cleanup)
    {
        definition.Validate(); snapshot.Validate(definition);
        this.definition = definition; Snapshot = snapshot; this.cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
    }
    public MinigamePhase Phase { get; private set; } = MinigamePhase.Starting;
    public MinigameSnapshot Snapshot { get; private set; }
    public MinigameResult? Result { get; private set; }
    public bool IsActive => Phase is MinigamePhase.Starting or MinigamePhase.Running or MinigamePhase.Paused;
    public void Start() { if (Phase != MinigamePhase.Starting) throw new InvalidOperationException("Мини-игра уже запущена."); Phase = MinigamePhase.Running; }
    public void Pause(bool paused)
    { if (Phase is MinigamePhase.Running or MinigamePhase.Paused) Phase = paused ? MinigamePhase.Paused : MinigamePhase.Running; }
    public void Commit(LuaValue state, LuaRandomState random)
    {
        if (Phase is not (MinigamePhase.Starting or MinigamePhase.Running)) throw new InvalidOperationException("Мини-игра не принимает изменения.");
        var next = Snapshot with { State = state, RandomState = random, Initialized = true }; next.Validate(definition); Snapshot = next;
    }
    public bool Finish(MinigameResult result)
    {
        if (!IsActive) return false;
        result.Validate(definition); Result = result; Phase = result.Phase;
        Interlocked.Exchange(ref cleanup, null)?.Invoke(); return true;
    }
    public void Dispose()
    {
        if (Phase == MinigamePhase.Disposed) return;
        try { if (IsActive) Finish(new(MinigamePhase.Cancelled, LuaValue.Null, "Мини-игра закрыта.")); }
        finally { Phase = MinigamePhase.Disposed; Interlocked.Exchange(ref cleanup, null)?.Invoke(); }
    }
}
