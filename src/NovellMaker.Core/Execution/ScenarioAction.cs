using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Execution;

// Actions are data. Hosts and Lua submit these same commands to ScenarioPlayer.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextAction), "text")]
[JsonDerivedType(typeof(ShowAction), "show")]
[JsonDerivedType(typeof(HideAction), "hide")]
[JsonDerivedType(typeof(StateAction), "state")]
[JsonDerivedType(typeof(PlaceAction), "place")]
[JsonDerivedType(typeof(MoveAction), "move")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(ChoiceAction), "choice")]
[JsonDerivedType(typeof(JumpAction), "jump")]
[JsonDerivedType(typeof(SetAction), "set")]
[JsonDerivedType(typeof(BranchAction), "branch")]
[JsonDerivedType(typeof(ScriptAction), "script")]
[JsonDerivedType(typeof(RunScriptAction), "runScript")]
[JsonDerivedType(typeof(HotspotAction), "hotspot")]
[JsonDerivedType(typeof(LineEntryAction), "lineEntry")]
public abstract record ScenarioAction([property: JsonRequired] Guid Id);
public sealed record LineEntryAction(Guid Id, [property: JsonRequired] Guid LineId) : ScenarioAction(Id);
public sealed record TextAction(Guid Id, [property: JsonRequired] Guid LineId) : ScenarioAction(Id);
public sealed record ShowAction(Guid Id, [property: JsonRequired] Guid ObjectId) : ScenarioAction(Id);
public sealed record HideAction(Guid Id, [property: JsonRequired] Guid ObjectId) : ScenarioAction(Id);
public sealed record StateAction(Guid Id, [property: JsonRequired] Guid ObjectId, [property: JsonRequired] Guid StateId) : ScenarioAction(Id);
public sealed record PlaceAction(Guid Id, [property: JsonRequired] Guid ObjectId, [property: JsonRequired] CharacterPlacement? Placement) : ScenarioAction(Id);
public sealed record MoveAction(Guid Id, [property: JsonRequired] Guid ObjectId, [property: JsonRequired] CharacterPlacement Target,
    [property: JsonRequired] double Seconds) : ScenarioAction(Id);
// mode: input, timer, actions, interaction. Interaction token is a hotspot's stable ID.
public sealed record WaitAction(Guid Id, [property: JsonRequired] string Mode, double Seconds = 0, Guid? Token = null) : ScenarioAction(Id);
public sealed record ChoiceOption([property: JsonRequired] Guid Id, [property: JsonRequired] string Text,
    [property: JsonRequired] Guid TargetSceneId, [property: JsonRequired] Guid TargetActionId);
public sealed record ChoiceAction(Guid Id, [property: JsonRequired] ImmutableArray<ChoiceOption> Options) : ScenarioAction(Id);
public sealed record JumpAction(Guid Id, [property: JsonRequired] Guid SceneId, [property: JsonRequired] Guid ActionId) : ScenarioAction(Id);
// Values are deliberately scalar strings, with ordinal comparison; Lua converts explicitly.
public sealed record SetAction(Guid Id, [property: JsonRequired] string Variable, [property: JsonRequired] string Value) : ScenarioAction(Id);
public sealed record BranchAction(Guid Id, [property: JsonRequired] string Variable, [property: JsonRequired] string EqualsValue,
    [property: JsonRequired] Guid SceneId, [property: JsonRequired] Guid ActionId) : ScenarioAction(Id);
public sealed record ScriptAction(Guid Id, [property: JsonRequired] string Source, [property: JsonRequired] string Name) : ScenarioAction(Id)
{
    public ImmutableDictionary<string, Guid> References { get; init; } = ImmutableDictionary<string, Guid>.Empty;
}
public sealed record RunScriptAction(Guid Id, [property: JsonRequired] Guid ScriptId) : ScenarioAction(Id);
// Normalized scene rectangle, optionally drawn as a labelled button by the host.
public sealed record HotspotAction(Guid Id, [property: JsonRequired] Guid ObjectId, [property: JsonRequired] string Label,
    [property: JsonRequired] double X, [property: JsonRequired] double Y, [property: JsonRequired] double Width,
    [property: JsonRequired] double Height, bool Enabled = true) : ScenarioAction(Id);
