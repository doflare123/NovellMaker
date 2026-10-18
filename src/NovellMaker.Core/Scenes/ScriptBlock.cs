using System.Text.Json.Serialization;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Scenes;

[JsonConverter(typeof(JsonStringEnumConverter<ScriptPhase>))]
public enum ScriptPhase { SceneStart, BeforeLine, DuringLine, AfterLine, SceneEnd }

/// <summary>Author-facing cue attached to a stable phrase; its action runs in the common interpreter.</summary>
public sealed record ScriptBlock([property: JsonRequired] Guid? LineId,
    [property: JsonRequired] ScriptPhase Phase, [property: JsonRequired] ScenarioAction Action);
