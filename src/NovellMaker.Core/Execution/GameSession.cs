using System.Collections.Immutable;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Execution;

public enum GameSessionPhase { Menu, Playing, Ended }

/// <summary>Host-thread lifetime of a playthrough. Menu canvases never execute the scenario.</summary>
public sealed class GameSession : IDisposable, IAsyncDisposable
{
    private readonly NovelProject project;
    private readonly Guid startSceneId;
    private readonly string language;
    private readonly PlaybackMode playbackMode;
    private readonly Func<ScriptAction, GameState, NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> runLua;
    private readonly List<Task> retiring = [];
    private bool disposed;
    private bool changing;
    private readonly object loadOwner = new();
    public ScenePlayer Player { get; private set; }
    public GameSessionPhase Phase { get; private set; }
    public long Generation { get; private set; }
    public Guid SessionId { get; private set; } = Guid.NewGuid();
    public ImmutableArray<string> CleanupDiagnostics { get; private set; } = [];
    /// <summary>Best-effort cleanup before publication. Every subscriber runs; failures are reported in CleanupDiagnostics, never rolled back.</summary>
    public event Action? Resetting;

    public GameSession(NovelProject project, Guid startSceneId, string language,
        Guid? startLineId = null, PlaybackMode playbackMode = PlaybackMode.Game)
        : this(project, startSceneId, language, startLineId, playbackMode, LuaActionScript.RequestCommandsAsync) { }

    internal GameSession(NovelProject project, Guid startSceneId, string language, Guid? startLineId, PlaybackMode playbackMode,
        Func<ScriptAction, GameState, NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> runLua)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.startSceneId = startSceneId; this.language = language; this.playbackMode = playbackMode; this.runLua = runLua;
        if (!project.Scenes.Any(s => s.Id == startSceneId)) throw new ArgumentException("Стартовая сцена отсутствует.");
        if (project.StartScreenSceneId is { } menu && !project.Scenes.Any(s => s.Id == menu)) throw new ArgumentException("Стартовый экран отсутствует.");
        Phase = project.StartScreenSceneId is null ? GameSessionPhase.Playing : GameSessionPhase.Menu;
        Player = CreatePlayer(project.StartScreenSceneId ?? startSceneId, Phase == GameSessionPhase.Playing,
            Phase == GameSessionPhase.Playing ? startLineId : null);
    }

    private ScenePlayer CreatePlayer(Guid scene, bool start, Guid? line = null) =>
        new(project, scene, language, line, playbackMode, start, runLua);

    // The expected generation belongs to the input/operation that requested the transition.
    public bool NewGame(long expectedGeneration, Guid? sceneId = null)
    {
        if (!Current(expectedGeneration) || Phase != GameSessionPhase.Menu) return false;
        var target = sceneId ?? startSceneId;
        if (!project.Scenes.Any(s => s.Id == target)) return false;
        // Prepare a dormant player before retiring the old lifetime. Start only after host cleanup.
        var next = CreatePlayer(target, false);
        Replace(next, GameSessionPhase.Playing);
        next.Execution.IsPaused = false;
        next.EnterScene(target);
        return true;
    }

    public bool ReturnToMenu(long expectedGeneration)
    {
        if (!Current(expectedGeneration) || Phase != GameSessionPhase.Playing || project.StartScreenSceneId is not { } menu) return false;
        Replace(CreatePlayer(menu, false), GameSessionPhase.Menu);
        return true;
    }

    public bool NavigateMenu(Guid sceneId)
    {
        if (disposed || changing || Phase != GameSessionPhase.Menu || !project.Scenes.Any(s => s.Id == sceneId)) return false;
        Replace(CreatePlayer(sceneId, false), GameSessionPhase.Menu);
        return true;
    }

    /// <summary>Existing quickslot bridge. Validate without replay; a failed load preserves the entire live lifetime.</summary>
    public void LoadSave(string json)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (changing) throw new InvalidOperationException("Смена сеанса ещё выполняется.");
        if (Phase == GameSessionPhase.Ended) throw new InvalidOperationException("Сеанс завершён.");
        var next = CreatePlayer(startSceneId, false);
        try { next.LoadSave(json); next.Execution.IsPaused = false; }
        catch { next.Dispose(); throw; }
        Replace(next, GameSessionPhase.Playing);
    }

    /// <summary>The generation must be captured when the asynchronous operation starts, not when it completes.</summary>
    public bool TryLoadSave(string json, long expectedGeneration)
    {
        if (!Current(expectedGeneration)) return false;
        LoadSave(json);
        return true;
    }

    internal GameLoadPreparation CaptureLoadPreparation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new(loadOwner, project, startSceneId, language, playbackMode, runLua);
    }

    /// <summary>Host commit only: ownership/generation checks and lifetime replacement, no parsing or project work.</summary>
    internal bool TryLoadPrepared(PreparedGameLoad prepared, long expectedGeneration)
    {
        if (!Current(expectedGeneration)) return false;
        Replace(prepared.Take(loadOwner), GameSessionPhase.Playing);
        return true;
    }

    public bool End(long expectedGeneration)
    {
        if (!Current(expectedGeneration) || Phase == GameSessionPhase.Ended) return false;
        Replace(CreatePlayer(project.StartScreenSceneId ?? startSceneId, false), GameSessionPhase.Ended);
        return true;
    }

    private bool Current(long expected) => !disposed && !changing && Generation == expected && Phase != GameSessionPhase.Ended;
    public bool IsCurrentGeneration(long expectedGeneration) => Current(expectedGeneration);
    private void Replace(ScenePlayer next, GameSessionPhase phase)
    {
        changing = true;
        try
        {
            Generation++; SessionId = Guid.NewGuid();
            Retire(Player);
            ResetHost();
            Player = next; Phase = phase;
        }
        finally { changing = false; }
    }
    private void ResetHost()
    {
        CleanupDiagnostics = [];
        if (Resetting is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error)
            {
                // Cleanup may already have side effects. Complete the transition and expose the failure.
                if (CleanupDiagnostics.Length < 16) CleanupDiagnostics = CleanupDiagnostics.Add(error.Message.Length <= 1024 ? error.Message : error.Message[..1024]);
            }
        }
    }
    private void Retire(ScenePlayer player)
    {
        player.Dispose();
        retiring.RemoveAll(t => t.IsCompleted);
        var cleanup = player.DisposeAsync().AsTask();
        _ = cleanup.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        retiring.Add(cleanup);
    }
    public void Dispose()
    {
        if (disposed) return;
        if (changing) throw new InvalidOperationException("Нельзя уничтожить сеанс внутри обработчика его замены.");
        disposed = true; Generation++; Phase = GameSessionPhase.Ended;
        Retire(Player); ResetHost();
    }
    public async ValueTask DisposeAsync() { Dispose(); await Task.WhenAll(retiring).ConfigureAwait(false); }
}
