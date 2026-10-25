using System.Collections.Immutable;

namespace NovellMaker.Core.Scripting;

/// <summary>Worker data only. Identity/event context is always supplied by the host.</summary>
public sealed record LuaLogMessage(string Level, string Message, Guid? ScriptId = null, int? Line = null)
{
    public const int MaximumMessageLength = 1024;
    internal void Validate()
    {
        if (Level is not ("info" or "warning" or "error") || Message is null || Message.Length > MaximumMessageLength ||
            ScriptId == Guid.Empty || Line is <= 0 or > 32769)
            throw new ArgumentException("Некорректная запись журнала Lua.");
    }
}

public sealed record LuaDiagnosticEntry(long Sequence, DateTimeOffset TimeUtc, Guid SessionId, long Generation,
    Guid OwnerId, Guid InstanceId, Guid SceneId, string Event, LuaLogMessage Data)
{
    // JSON string escaping keeps multiline/control characters inside one physical line.
    public string Format() => $"{Sequence} {TimeUtc:O} {Data.Level} session={SessionId:N}/{Generation} owner={OwnerId:N} instance={InstanceId:N} scene={SceneId:N} script={Data.ScriptId?.ToString("N") ?? "unknown"} event={Event} line={Data.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} {System.Text.Json.JsonSerializer.Serialize(Data.Message)}";
}

/// <summary>Host-thread ring. Diagnostic effects are independent of transactional game commands.</summary>
public sealed class LuaDiagnosticJournal
{
    public const int MaximumEntries = 256;
    public const int MaximumBatchEntries = 32;
    public const int MaximumBatchCharacters = 8192;
    private readonly Queue<LuaDiagnosticEntry> entries = [];
    public long Sequence { get; private set; }
    public long Evicted { get; private set; }
    public ImmutableArray<LuaDiagnosticEntry> Entries => [.. entries];

    internal void Add(Guid sessionId, long generation, Guid ownerId, Guid instanceId, Guid sceneId, string eventName, LuaLogMessage message)
    {
        message.Validate();
        if (entries.Count == MaximumEntries) { entries.Dequeue(); Evicted++; }
        entries.Enqueue(new(++Sequence, DateTimeOffset.UtcNow, sessionId, generation, ownerId, instanceId, sceneId, eventName, message));
    }
}
