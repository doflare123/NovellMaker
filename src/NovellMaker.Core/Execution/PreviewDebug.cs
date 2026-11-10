using System.Collections.Immutable;
using NovellMaker.Core.Minigames;

namespace NovellMaker.Core.Execution;

/// <summary>Author-defined entry into a fresh playthrough of the current project revision.</summary>
public sealed record PreviewStart(Guid? ActionId, ImmutableDictionary<string, string> Variables, MinigameOutcome? Outcome = null);

public sealed record PreviewTrace(string Message, string? Variable = null, string? Value = null,
    ImmutableDictionary<string, string>? Snapshot = null, MinigameOutcome? Outcome = null);
