using System.Collections.Immutable;
using System.Diagnostics;

namespace NovellMaker.Core.Execution;

public sealed partial class ScenarioPlayer
{
    private readonly Func<ScriptAction, GameState, Projects.NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> runLua;
    private sealed record PendingLua(long Generation, GameState Before, GameState After,
        CancellationTokenSource Cancellation, Task<ImmutableArray<ScenarioAction>> Task);
    private PendingLua? pendingLua;
    private readonly List<Task> retiringLua = [];
    private long generation;
    private bool disposed;
    private Stopwatch? passTime;
    private int passLuaCalls, passActions;
    private bool isPaused;
    public bool IsPaused
    {
        get => isPaused;
        set { if (isPaused == value) return; isPaused = value; if (value) passTime?.Stop(); else passTime?.Start(); }
    }
    public bool HasPendingLua => pendingLua is not null;
    /// <summary>Host may await completion, but only PollLua/Tick applies it on the owning thread.</summary>
    public Task? PendingLuaTask => pendingLua?.Task;

    private void ResetPass() { passTime = null; passLuaCalls = 0; passActions = 0; }

    private void BeginLua(ScenarioAction action, GameState before, GameState after)
    {
        var script = action as ScriptAction;
        if (action is RunScriptAction call)
        {
            var resource = project.Scripts.FirstOrDefault(s => s.Id == call.ScriptId)
                ?? throw new InvalidOperationException($"Ресурс Lua {call.ScriptId} отсутствует.");
            script = new(call.Id, resource.Source, resource.Name) { References = resource.References };
        }
        var cancellation = new CancellationTokenSource();
        pendingLua = new(generation, before, after, cancellation, PrepareLuaAsync(script!, before, after, cancellation.Token));
    }

    private async Task<ImmutableArray<ScenarioAction>> PrepareLuaAsync(ScriptAction script, GameState before, GameState after, CancellationToken token)
    {
        var result = await runLua(script, before, project, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var commands = LuaActionScript.ValidateCommands(result, before, project);
            if (commands.Length + after.QueuedActions.Length > 4096)
                throw new InvalidOperationException("Превышен лимит очереди Lua (4096 команд).");
            if (!LuaLimits.ValidQueue(commands.Concat(after.QueuedActions))) throw new InvalidOperationException("Превышен размер очереди Lua.");
            token.ThrowIfCancellationRequested();
            return commands.AddRange(after.QueuedActions);
        }, token).ConfigureAwait(false);
    }

    /// <summary>Nonblocking. Called exclusively by the host on its game/UI thread; never from a Task continuation.</summary>
    public bool PollLua()
    {
        if (disposed || IsPaused || pendingLua is not { } pending || !pending.Task.IsCompleted) return false;
        if (pending.Generation != generation || !ReferenceEquals(State, pending.Before)) { CancelPendingLua(); return false; }
        pendingLua = null;
        try
        {
            // IsCompleted was checked above; this never waits on a worker.
            var queue = pending.Task.GetAwaiter().GetResult();
            State = pending.After with { QueuedActions = queue };
            Pump();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException)
        { State = pending.Before; Fail($"Сцена {State.SceneId}: {error.Message}"); }
        finally { pending.Cancellation.Dispose(); }
        return true;
    }

    private void CancelPendingLua()
    {
        generation++;
        if (pendingLua is not { } pending) return;
        pendingLua = null;
        pending.Cancellation.Cancel();
        pending.Cancellation.Dispose();
        retiringLua.RemoveAll(t => t.IsCompleted);
        retiringLua.Add(pending.Task);
        _ = pending.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void EnsureCanSave()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (HasPendingLua) throw new InvalidOperationException("Lua ещё выполняется. Повторите сохранение после завершения операции.");
    }

    public void Dispose() { if (disposed) return; disposed = true; CancelPendingLua(); }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (retiringLua.Count == 0) return;
        var work = Task.WhenAll(retiringLua);
        _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await work.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ArgumentException or TimeoutException) { }
    }
}
