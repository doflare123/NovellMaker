using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeScreens
{
    private Func<ScenarioPlayer>? execution;
    private ScenarioPlayer? minigamePlayer;
    private ScreenInstance? minigameInstance;
    internal MinigameRun? ActiveMinigame { get; private set; }
    private bool minigameHostActive = true;
    private double minigameElapsed;
    private int observedBatches;

    /// <summary>Advance this frame before admitting its Lua update; an idle handler cannot starve a tween.</summary>
    public void Tick(TimeSpan elapsed, bool hostActive)
    {
        minigameHostActive = hostActive;
        SyncMinigame();
        TickAnimations(elapsed, hostActive);
        TickMinigame(elapsed, hostActive);
    }

    /// <summary>One coalesced elapsed update, at the resource rate. Busy time accumulates up to one second.</summary>
    public void TickMinigame(TimeSpan elapsed, bool hostActive)
    {
        minigameHostActive = hostActive;
        SyncMinigame();
        if (ActiveMinigame?.Phase != MinigamePhase.Running || minigameInstance is null || Scripts is null) return;
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        minigameElapsed = Math.Min(1, minigameElapsed + seconds);
        var definition = project!.Scenes.Single(s => s.Id == ActiveMinigame.Snapshot.SceneId).Canvas.Minigame!;
        if (minigameElapsed < 1 / definition.UpdatesPerSecond || !Scripts.IsSettled(minigameInstance)) return;
        if (Scripts.Enqueue(minigameInstance, new("update", Seconds: minigameElapsed), snapshot()) == ScreenEventAdmission.Accepted)
        { minigameElapsed = 0; }
    }

    private void SyncMinigame()
    {
        var player = execution?.Invoke();
        var saved = player?.State.Minigame;
        if (ActiveMinigame is { } old && (!ReferenceEquals(player, minigamePlayer) || saved?.RunId != old.Snapshot.RunId)) DisposeMinigame();
        if (saved is null || player is null) return;
        if (ActiveMinigame is null)
        {
            var asset = project!.Scenes.Single(s => s.Id == saved.SceneId).AsScreen();
            ScreenInstance? instance = null;
            try
            {
                instance = Stack.Open(Guid.NewGuid(), asset.Definition(), new(true, true));
                var captured = instance;
                minigamePlayer = player; minigameInstance = instance;
                ActiveMinigame = new(asset.Minigame!, saved, () => Stack.CloseOwner(captured.OwnerId));
                instanceGenerations[instance.Id] = generation();
                var script = project.Scripts.Single(s => s.Id == asset.HandlerScriptId);
                if (Scripts is null) throw new InvalidOperationException("Lua-worker мини-игры недоступен.");
                Scripts.Attach(instance, script.Source, asset.References, snapshot(), saved.State,
                    new LuaModuleLibrary(script.Id, project.Scripts), saved.RandomState, asset.Minigame, new(saved.Parameters, player.MinigameRestored));
                observedBatches = Scripts.AppliedBatches;
                player.MinigameSaveBarrier = () => minigameInstance is not null && Scripts?.HasAnimations(minigameInstance) == true
                    ? "Дождитесь завершения анимации мини-игры перед сохранением."
                    : minigameInstance is null || Scripts?.IsSettled(minigameInstance) != true || ActiveMinigame?.Phase == MinigamePhase.Starting
                    ? "Обработчик мини-игры ещё выполняется. Повторите сохранение после завершения операции." : null;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                if (instance is not null && Stack.Instances.Any(i => ReferenceEquals(i, instance))) Stack.Close(instance);
                EndMinigame(new(MinigamePhase.Failed, LuaValue.Null, Clip(error.Message))); Report(error.Message);
                return;
            }
        }
        if (minigameInstance is null || Scripts is null) return;
        if (!Stack.Instances.Any(i => ReferenceEquals(i, minigameInstance)))
        { EndMinigame(new(MinigamePhase.Cancelled, LuaValue.Null, "Экран мини-игры закрыт.")); return; }
        var paused = !minigameHostActive || !ReferenceEquals(Stack.Instances.LastOrDefault(), minigameInstance);
        ActiveMinigame!.Pause(paused); Scripts.Suspend(minigameInstance, paused);
    }

    private void CommitMinigame()
    {
        if (ActiveMinigame is not { } run || minigameInstance is not { } instance || Scripts is null) return;
        if (!Scripts.IsAvailable(instance))
        { EndMinigame(new(MinigamePhase.Failed, LuaValue.Null, Clip(Scripts.Issues.LastOrDefault()?.Message ?? "Обработчик мини-игры завершился с ошибкой."))); return; }
        if (run.Phase == MinigamePhase.Paused) return;
        if (Scripts.AppliedBatches != observedBatches)
        {
            observedBatches = Scripts.AppliedBatches;
            run.Commit(Scripts.GetState(instance), Scripts.GetRandomState(instance));
            minigamePlayer!.CommitMinigame(run.Snapshot);
        }
        if (run.Phase == MinigamePhase.Starting && Scripts.IsSettled(instance)) run.Start();
        if (Scripts.GetMinigameResult(instance) is { } result) EndMinigame(result);
    }

    private static string Clip(string value) => value.Length <= 1024 ? value : value[..1024];
    private void EndMinigame(MinigameResult result)
    {
        var run = ActiveMinigame; var player = minigamePlayer ?? execution?.Invoke();
        var id = run?.Snapshot.RunId ?? player?.State.Minigame?.RunId;
        run?.Finish(result);
        DisposeMinigame();
        if (id is { } runId && player is not null)
        { player.IsPaused = Stack.PausesScenario || !minigameHostActive; player.FinishMinigame(runId, result); }
    }
    private void DisposeMinigame()
    {
        var run = ActiveMinigame;
        ActiveMinigame = null; minigameInstance = null; minigameElapsed = 0;
        if (minigamePlayer is { } player) player.MinigameSaveBarrier = null;
        minigamePlayer = null; run?.Dispose();
    }
}
