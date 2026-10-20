using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Execution;

/// <summary>Only data readable by game.*; no queued actions, dialogue, assets or other script sources.</summary>
public sealed record LuaStateSnapshot(double Time, ImmutableDictionary<string, string> Variables,
    ImmutableArray<Guid> VisibleObjects, ImmutableDictionary<Guid, Guid> ObjectStates,
    ImmutableDictionary<Guid, CharacterPlacement> Placements)
{
    internal static LuaStateSnapshot Capture(GameState state) =>
        new(state.Time, state.Variables, state.VisibleObjects, state.ObjectStates, state.Placements);
}

/// <summary>Reference permissions, independent of novel text and resource payloads.</summary>
public sealed record LuaCommandCatalog(ImmutableArray<Guid> Lines,
    ImmutableDictionary<Guid, ImmutableArray<Guid>> ObjectStates,
    ImmutableDictionary<Guid, ImmutableArray<Guid>> SceneActions, ImmutableArray<Guid> Scripts)
{
    // Reference identity, not record equality/fingerprint. The cache does not retain old projects.
    private static readonly ConditionalWeakTable<NovelProject, ProjectCache> Cache = new();

    internal static LuaCommandCatalog Capture(NovelProject project, Guid sceneId, CancellationToken cancellationToken = default)
        => Capture(project, sceneId, LuaRequestPreparation.ForCancellation(cancellationToken));

    internal static LuaCommandCatalog Capture(NovelProject project, Guid sceneId, LuaRequestPreparation preparation)
    {
        preparation.Checkpoint();
        return Cache.GetValue(project, static _ => new()).Get(project, sceneId, preparation);
    }

    private sealed class ProjectCache
    {
        private readonly SemaphoreSlim gate = new(1);
        private ImmutableDictionary<Guid, LuaCommandCatalog>? catalogs;

        internal LuaCommandCatalog Get(NovelProject project, Guid sceneId, LuaRequestPreparation preparation)
        {
            // Waiting behind another builder is cancellable too; no host/UI thread calls this path.
            gate.Wait(preparation.CancellationToken);
            try
            {
                preparation.Checkpoint();
                var prepared = catalogs ?? Build(project, preparation);
                preparation.Checkpoint();
                // Publish only a completely built, still-current result. Cancellation never poisons the cache.
                catalogs = prepared;
                return prepared.TryGetValue(sceneId, out var catalog) ? catalog :
                    throw new InvalidOperationException("Сцена каталога Lua отсутствует в проекте.");
            }
            finally { gate.Release(); }
        }

        private static ImmutableDictionary<Guid, LuaCommandCatalog> Build(NovelProject project, LuaRequestPreparation preparation)
        {
            var objects = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<Guid>>();
            foreach (var character in project.Characters)
            {
                preparation.Checkpoint();
                var states = ImmutableArray.CreateBuilder<Guid>();
                foreach (var state in character.States) { preparation.Checkpoint(); states.Add(state.Id); }
                objects.Add(character.Id, states.ToImmutable());
            }
            var lines = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<Guid>>();
            var targets = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<Guid>>();
            foreach (var scene in project.Scenes)
            {
                preparation.Checkpoint();
                var sceneLines = ImmutableArray.CreateBuilder<Guid>();
                foreach (var line in scene.Lines) { preparation.Checkpoint(); sceneLines.Add(line.Id); }
                lines.Add(scene.Id, sceneLines.ToImmutable());
                var actions = ImmutableArray.CreateBuilder<Guid>();
                foreach (var action in ScenarioProgram.EnumerateActions(scene, preparation.Checkpoint))
                { preparation.Checkpoint(); actions.Add(action.Id); }
                targets.Add(scene.Id, actions.ToImmutable());
            }
            var scripts = ImmutableArray.CreateBuilder<Guid>();
            foreach (var script in project.Scripts) { preparation.Checkpoint(); scripts.Add(script.Id); }
            var objectStates = objects.ToImmutable(); var sceneActions = targets.ToImmutable(); var scriptIds = scripts.ToImmutable();
            var result = ImmutableDictionary.CreateBuilder<Guid, LuaCommandCatalog>();
            foreach (var (id, sceneLines) in lines)
            {
                preparation.Checkpoint();
                result.Add(id, new(sceneLines, objectStates, sceneActions, scriptIds));
            }
            preparation.Checkpoint();
            return result.ToImmutable();
        }
    }

    internal bool IsValid(ScenarioAction action) => ScenarioProgram.IsValidAction(action,
        ObjectStates.ContainsKey, (id, state) => ObjectStates.TryGetValue(id, out var states) && states.Contains(state),
        Lines.Contains, (scene, id) => SceneActions.TryGetValue(scene, out var actions) && actions.Contains(id), Scripts.Contains);
}
