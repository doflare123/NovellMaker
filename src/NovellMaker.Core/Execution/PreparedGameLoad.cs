using System.Collections.Immutable;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Execution;

/// <summary>Exclusive ownership of a dormant, fully restored player; never exposes an unchecked state.</summary>
internal sealed class PreparedGameLoad(object owner, ScenePlayer player) : IDisposable
{
    private ScenePlayer? candidate = player;
    internal bool IsAvailable => candidate is not null;
    internal ScenePlayer Take(object expectedOwner)
    {
        if (!ReferenceEquals(owner, expectedOwner)) throw new ArgumentException("Подготовка относится к другому сеансу.");
        return Interlocked.Exchange(ref candidate, null) ?? throw new InvalidOperationException("Подготовка загрузки уже использована.");
    }
    public void Dispose() => Interlocked.Exchange(ref candidate, null)?.Dispose();
}

/// <summary>Immutable host capture. Background preparation never reads the live session or player.</summary>
internal sealed record GameLoadPreparation(object Owner, NovelProject Project, Guid StartSceneId, string Language,
    PlaybackMode Mode, Func<ScriptAction, GameState, NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> RunLua)
{
    internal Task<PreparedGameLoad> PrepareAsync(string json, CancellationToken token) => GameSave.PrepareAsync(() =>
    {
        // Construction compiles programs/fingerprints, and LoadSave validates and prepares the frame.
        // startImmediately=false prevents all commands, Lua and time advancement in this isolated player.
        var next = new ScenePlayer(Project, StartSceneId, Language, null, Mode, false, RunLua);
        try
        {
            next.LoadSave(json);
            next.Execution.IsPaused = false;
            return new PreparedGameLoad(Owner, next);
        }
        catch { next.Dispose(); throw; }
    }, token);
}
