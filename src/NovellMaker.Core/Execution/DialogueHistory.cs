using System.Collections.Immutable;

namespace NovellMaker.Core.Execution;

/// <summary>A visit, not a rollback point. Text and voice resolve against the save's exact project revision.</summary>
public sealed record DialogueHistoryEntry(Guid VisitId, Guid SceneId, Guid LineId, string Language);

public static class DialogueHistory
{
    public const int MaximumEntries = 500;
    public static ImmutableArray<DialogueHistoryEntry> Append(ImmutableArray<DialogueHistoryEntry> history,
        Guid scene, Guid line, string language)
    {
        if (history.Length == MaximumEntries) history = history.RemoveAt(0);
        return history.Add(new(Guid.NewGuid(), scene, line, language));
    }
}
