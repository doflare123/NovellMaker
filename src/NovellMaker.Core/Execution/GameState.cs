using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Execution;

public sealed record ActiveMove(Guid ActionId, Guid ObjectId, CharacterPlacement From, CharacterPlacement Target, double Duration, double Elapsed);
public sealed record PendingWait(Guid ActionId, string Mode, double Remaining = 0, Guid? Token = null);

/// <summary>Serializable game state. Contains no host resources, delegates, Lua VM or Lua stack.</summary>
public sealed record GameState
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NovellMaker.Core.Minigames.MinigameSnapshot? Minigame { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NovellMaker.Core.Minigames.MinigameOutcome? MinigameOutcome { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Revision { get; init; }
    public required Guid SceneId { get; init; }
    [JsonRequired] public Guid? NextActionId { get; init; }
    [JsonRequired] public Guid? LineId { get; init; }
    [JsonRequired] public double Time { get; init; }
    [JsonRequired] public double TextStartedAt { get; init; }
    [JsonRequired] public bool Completed { get; init; }
    [JsonRequired] public string? Error { get; init; }
    [JsonRequired] public ImmutableDictionary<string, string> Variables { get; init; } = ImmutableDictionary<string, string>.Empty;
    [JsonRequired] public ImmutableDictionary<Guid, Guid> ObjectStates { get; init; } = ImmutableDictionary<Guid, Guid>.Empty;
    [JsonRequired] public ImmutableArray<Guid> VisibleObjects { get; init; } = [];
    [JsonRequired] public ImmutableDictionary<Guid, CharacterPlacement> Placements { get; init; } = ImmutableDictionary<Guid, CharacterPlacement>.Empty;
    [JsonRequired] public ImmutableArray<ActiveMove> ActiveActions { get; init; } = [];
    [JsonRequired] public ImmutableArray<ScenarioAction> QueuedActions { get; init; } = [];
    [JsonRequired] public ImmutableArray<HotspotAction> Hotspots { get; init; } = [];
    [JsonRequired] public PendingWait? Wait { get; init; }
    [JsonRequired] public ChoiceAction? Choice { get; init; }
    [JsonRequired] public ImmutableArray<string> Diagnostics { get; init; } = [];
}

