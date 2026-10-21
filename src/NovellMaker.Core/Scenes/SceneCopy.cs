using System.Collections.Immutable;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Scenes;

public static class SceneCopy
{
    public static (NovelScene Scene, ImmutableDictionary<Guid, Guid> Ids) Create(NovelScene source)
    {
        var ids = new Dictionary<Guid, Guid> { [source.Id] = Guid.NewGuid() };
        foreach (var line in source.Lines)
        {
            ids[line.Id] = Guid.NewGuid();
            ids[ScenarioProgram.LineEntryId(line.Id)] = ScenarioProgram.LineEntryId(ids[line.Id]);
        }
        foreach (var node in source.Canvas.Nodes) ids[node.Id] = Guid.NewGuid();
        foreach (var action in source.Actions.Concat(source.ScriptBlocks.Select(b => b.Action))) ids.TryAdd(action.Id, Guid.NewGuid());
        Guid Map(Guid id) => ids.GetValueOrDefault(id, id);
        ScenarioAction Copy(ScenarioAction action) => (action with { Id = Map(action.Id) }) switch
        {
            TextAction a => a with { LineId = Map(a.LineId) },
            LineEntryAction a => a with { LineId = Map(a.LineId) },
            JumpAction a => a with { SceneId = Map(a.SceneId), ActionId = Map(a.ActionId) },
            BranchAction a => a with { SceneId = Map(a.SceneId), ActionId = Map(a.ActionId) },
            ChoiceAction a => a with { Options = a.Options.Select(o => o with { Id = Guid.NewGuid(), TargetSceneId = Map(o.TargetSceneId), TargetActionId = Map(o.TargetActionId) }).ToImmutableArray() },
            WaitAction a => a with { Token = a.Token is { } token ? Map(token) : null },
            HotspotAction a => a with { ObjectId = Map(a.ObjectId) },
            ScriptAction a => a with { References = a.References.ToImmutableDictionary(r => r.Key, r => Map(r.Value)) },
            _ => action with { Id = Map(action.Id) }
        };
        return (source with
        {
            Id = Map(source.Id), Name = source.Name + " — копия",
            Lines = source.Lines.Select(l => l with { Id = Map(l.Id), TextKey = l.TextKey + "." + Map(l.Id).ToString("N") }).ToImmutableArray(),
            Actions = source.Actions.Select(Copy).ToImmutableArray(),
            ScriptBlocks = source.ScriptBlocks.Select(b => b with { LineId = b.LineId is { } line ? Map(line) : null, Action = Copy(b.Action) }).ToImmutableArray(),
            Canvas = source.Canvas with
            {
                Nodes = source.Canvas.Nodes.Select(n => n with { Id = Map(n.Id), ParentId = n.ParentId is { } parent ? Map(parent) : null }).ToImmutableArray(),
                References = source.Canvas.References.ToImmutableDictionary(r => r.Key, r => Map(r.Value)),
                Buttons = source.Canvas.Buttons.Select(b => b with { NodeId = Map(b.NodeId), TargetScreenId = b.TargetScreenId is { } target ? Map(target) : null }).ToImmutableArray()
            }
        }, ids.ToImmutableDictionary());
    }
}
