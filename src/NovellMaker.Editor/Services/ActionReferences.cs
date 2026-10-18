using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Editor.Services;

internal static class ActionReferences
{
    public static NovelProject Prune(NovelProject project)
    {
        bool removed;
        do
        {
            var source = project;
            bool Target(Guid sceneId, Guid actionId) => source.Scenes.Any(s => s.Id == sceneId && ScenarioProgram.Compile(s).Any(a => a.Id == actionId));
            bool Object(Guid id) => source.Characters.Any(c => c.Id == id);
            ScenarioAction? Clean(ScenarioAction action, NovelScene scene) => action switch
            {
                TextAction a when !scene.Lines.Any(l => l.Id == a.LineId) => null,
                ShowAction a when !Object(a.ObjectId) => null,
                HideAction a when !Object(a.ObjectId) => null,
                PlaceAction a when !Object(a.ObjectId) => null,
                MoveAction a when !Object(a.ObjectId) => null,
                StateAction a when !source.Characters.Any(c => c.Id == a.ObjectId && c.States.Any(s => s.Id == a.StateId)) => null,
                JumpAction a when !Target(a.SceneId, a.ActionId) => null,
                BranchAction a when !Target(a.SceneId, a.ActionId) => null,
                ChoiceAction a => a with { Options = a.Options.Where(o => Target(o.TargetSceneId, o.TargetActionId)).ToImmutableArray() },
                _ => action,
            };
            bool Keep(ScenarioAction? a) => a is not null && a is not ChoiceAction { Options.IsEmpty: true };
            project = source with { Scenes = source.Scenes.Select(scene => scene with
            {
                UseActions = scene.HasLegacyActionScript,
                Actions = scene.Actions.Select(a => Clean(a, scene)).Where(Keep).Select(a => a!).ToImmutableArray(),
                ScriptBlocks = scene.ScriptBlocks.Where(b => b.LineId is null || scene.Lines.Any(l => l.Id == b.LineId))
                    .Select(b => (Block: b, Action: Clean(b.Action, scene))).Where(p => Keep(p.Action)).Select(p => p.Block with { Action = p.Action! }).ToImmutableArray(),
            }).ToImmutableArray() };
            int Count(NovelProject p) => p.Scenes.Sum(s => s.Actions.Length + s.ScriptBlocks.Length);
            removed = Count(source) != Count(project);
        } while (removed);
        return project;
    }
}
