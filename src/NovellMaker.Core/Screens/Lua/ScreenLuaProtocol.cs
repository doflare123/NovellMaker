using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Triggers;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Core.Screens.Lua;

public sealed record ScreenLuaStamp(Guid RequestId, Guid SessionId, long Generation, Guid OwnerId, Guid InstanceId, long Version);
public sealed record ScreenLuaEvent(string Kind, Guid? NodeId = null, double X = 0, double Y = 0, double Seconds = 0)
{
    public GameServiceResult? Result { get; init; }
    public AuthorTrigger? Trigger { get; init; }
}
public sealed record ScreenChange(string Kind, Guid? NodeId, string? Text, bool? Value)
{
    public ScreenNodePatch? Patch { get; init; }
    public Guid? ParentId { get; init; }
    public ImmutableDictionary<Guid, Guid>? Ids { get; init; }
    public GameServiceCommand? Service { get; init; }
    public AuthorTrigger? Trigger { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LuaValue? State { get; init; }
}
internal sealed record ScreenLuaRequest(int Protocol, string Kind, ScreenLuaStamp Stamp, Guid ResourceId,
    ImmutableArray<ScreenNode> Nodes, string? Source, ImmutableDictionary<string, Guid> References,
    ScreenLuaEvent? Event, ImmutableDictionary<string, string> Snapshot)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LuaValue? State { get; init; }
}
internal sealed record ScreenLuaResponse(int Protocol, string Kind, ScreenLuaStamp Stamp, string? Error, ImmutableArray<ScreenChange> Changes);
internal sealed record ScreenPreparedBatch(ScreenDefinition Definition, bool Close, GameServiceCommand? Service = null)
{
    public ImmutableArray<AuthorTrigger> Triggers { get; init; } = [];
    public LuaValue? State { get; init; }
}
public sealed record ScreenServiceInvocation(ScreenInstance Instance, GameServiceCommand Command);

internal static class ScreenLuaProtocol
{
    internal const int Version = 9;
    internal const int MaximumFrameBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        using var buffer = new LuaWorkerClient.BoundedBuffer(MaximumFrameBytes);
        await JsonSerializer.SerializeAsync(buffer, message, Json, token).ConfigureAwait(false);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, checked((int)buffer.Length));
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is < 1 or > MaximumFrameBytes) throw new IOException("Превышен размер кадра экранного Lua.");
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new IOException("Пустое сообщение экранного Lua.");
    }

    /// <summary>Budget the whole service event, including JSON escaping and the unchanged variable snapshot.</summary>
    internal static async Task<ScreenLuaRequest> FitServiceResultAsync(ScreenLuaRequest request, CancellationToken token)
    {
        if (request.Event?.Result is not { Action: GameServiceAction.ListSlots } result || result.Slots.IsEmpty)
            return request;
        async Task<bool> Fits(ScreenLuaRequest candidate)
        {
            token.ThrowIfCancellationRequested();
            try { await WriteAsync(Stream.Null, candidate, token).ConfigureAwait(false); return true; }
            catch (IOException) { return false; } // Only the bounded in-memory serialization can fail here.
        }
        if (await Fits(request).ConfigureAwait(false)) return request;

        static string Clip(string text, int limit) => text.Length <= limit ? text :
            text[..(limit > 0 && char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];
        const string notice = "Текст списка сокращён до бюджета сообщения Lua.";
        var message = Clip(result.Message, 1024 - notice.Length - 1) + "\n" + notice;
        ScreenLuaRequest Project(int limit) => request with
        {
            Event = request.Event with { Result = result with
            {
                Message = message,
                Slots = result.Slots.Select(item => item with
                {
                    Diagnostic = item.Diagnostic is { } diagnostic ? Clip(diagnostic, limit) : null,
                    Metadata = item.Metadata is { } metadata ? metadata with
                    {
                        Title = Clip(metadata.Title, limit), SceneName = Clip(metadata.SceneName, limit),
                        Phrase = Clip(metadata.Phrase, limit),
                    } : null,
                }).ToImmutableArray(),
            } },
        };
        // IDs, statuses, revision, timestamps and every variable remain intact. Even the maximum
        // validated snapshot leaves room for all 128 rows with empty display strings.
        var best = Project(0);
        if (!await Fits(best).ConfigureAwait(false))
            throw new ArgumentException("Список слотов и снимок превышают общий бюджет сообщения Lua.");
        var lower = 1; var upper = 1024;
        while (lower <= upper)
        {
            var middle = (lower + upper) / 2;
            var candidate = Project(middle);
            if (await Fits(candidate).ConfigureAwait(false)) { best = candidate; lower = middle + 1; }
            else upper = middle - 1;
        }
        return best;
    }

    internal static void ValidateEvent(ScreenDefinition definition, ScreenLuaEvent value, bool worker = false)
    {
        if (value.Kind == "trigger")
        {
            if (value.Trigger is null || value.NodeId is not null || value.X != 0 || value.Y != 0 || value.Seconds != 0 || value.Result is not null)
                throw new ArgumentException("Некорректное событие триггера.");
            value.Trigger.Validate(); return;
        }
        if (value.Trigger is not null) throw new ArgumentException("Лишние данные триггера.");
        if (value.Kind == "service_result")
        {
            if (value.NodeId is not null || value.X != 0 || value.Y != 0 || value.Seconds != 0 || value.Result is null)
                throw new ArgumentException("Некорректное событие игрового сервиса.");
            value.Result.Validate(); return;
        }
        bool ValidClick(Guid? id)
        {
            if (id is null) return false;
            if (definition.Buttons.Any(n => n.Id == id)) return true;
            // The worker has authoring nodes, not host font metrics/current phrase/animation time.
            // Only the host admits rendered clicks. Worker still checks kind, flags and ancestry.
            if (!worker || definition.Nodes.FirstOrDefault(n => n.Id == id) is not { Kind: ScreenNodeKind.Button or ScreenNodeKind.Area } node) return false;
            var dialogue = false;
            var map = definition.Nodes.ToDictionary(n => n.Id);
            while (true)
            {
                if (!node.Enabled || !node.Visible || node.Geometry.Opacity <= 0) return false;
                dialogue |= node.Dialogue is DialogueBinding.Panel or DialogueBinding.CssPanel;
                if (node.ParentId is not { } parent) return dialogue;
                node = map[parent];
            }
        }
        if (value.Result is not null || value.Kind is not ("click" or "move" or "hover" or "update") ||
            !double.IsFinite(value.X) || !double.IsFinite(value.Y) || Math.Abs(value.X) > 100000 || Math.Abs(value.Y) > 100000 ||
            !double.IsFinite(value.Seconds) || value.Seconds is < 0 or > 1 ||
            value.NodeId is { } node && !definition.Nodes.Any(n => n.Id == node) ||
            value.Kind == "click" && !ValidClick(value.NodeId))
            throw new ArgumentException("Недопустимое событие экрана.");
    }

    internal static ScreenPreparedBatch Prepare(ScreenDefinition definition, ImmutableArray<ScreenChange> changes)
    {
        if (changes.IsDefault || changes.Length > LuaActionScript.MaximumCommands)
            throw new InvalidOperationException("Превышен бюджет изменений экрана.");
        if (changes.IsEmpty) return new(definition, false);
        var current = definition;
        var close = false;
        GameServiceCommand? service = null;
        var triggers = ImmutableArray.CreateBuilder<AuthorTrigger>();
        var characters = 0;
        var stateBytes = 0;
        LuaValue? state = null;
        foreach (var change in changes)
        {
            if (change is null || close) throw new InvalidOperationException("Некорректный пакет экрана.");
            var extra = change.Patch is not null || change.ParentId is not null || change.Ids is not null;
            if (change.Kind == "state" && change.State is { } data && !extra && change.NodeId is null && change.Text is null && change.Value is null && change.Service is null && change.Trigger is null)
            {
                if ((stateBytes += data.JsonByteCount) > LuaValue.MaximumJsonBytes)
                    throw new InvalidOperationException("Превышен бюджет состояний пакета Lua.");
                state = data; continue;
            }
            if (change.State is not null) throw new InvalidOperationException("Лишние данные состояния компонента.");
            if (change.Kind == "trigger" && change.Trigger is { } trigger && !extra && change.NodeId is null && change.Text is null && change.Value is null && change.Service is null)
            {
                trigger.Validate();
                if (triggers.Count >= AuthorTrigger.MaximumPerBatch) throw new InvalidOperationException("Превышен бюджет триггеров пакета.");
                triggers.Add(trigger); continue;
            }
            if (change.Trigger is not null) throw new InvalidOperationException("Лишние данные триггера.");
            if (change.Kind == "service" && change.Service is { } command && !extra && change.NodeId is null && change.Text is null && change.Value is null)
            {
                if (service is not null) throw new InvalidOperationException("Один Lua-пакет может запросить только одну операцию сервиса.");
                command.Validate(); service = command; continue;
            }
            if (change.Service is not null) throw new InvalidOperationException("Лишние данные команды сервиса.");
            if (change.Kind == "close" && change.NodeId is null && change.Text is null && change.Value is null && !extra)
            { close = true; continue; }
            if (change.NodeId is not { } id || current.Nodes.FirstOrDefault(n => n.Id == id) is not { } node)
                throw new InvalidOperationException("Чужой узел экрана.");
            if (change.Kind == "copy" && change.Text is null && change.Value is null && change.Patch is null && change.ParentId is { } parent && change.Ids is { } ids)
            { current = ScreenTree.Copy(current, id, parent, ids).Definition; continue; }
            if (change.Kind == "remove" && change.Text is null && change.Value is null && !extra)
            { current = ScreenTree.Remove(current, id); continue; }
            if (change.Kind == "parent" && change.Text is null && change.Value is null && change.Patch is null && change.Ids is null && change.ParentId is { } target)
            { current = ScreenTree.Reparent(current, id, target); continue; }
            ScreenNode updated;
            if (change.Kind == "geometry" && change.Text is null && change.Value is null && change.ParentId is null && change.Ids is null && change.Patch is { } patch)
                updated = patch.Apply(node);
            else if (change.Kind == "text" && !extra && change.Value is null && change.Text is { Length: <= 8192 } text)
            {
                characters += text.Length;
                if (characters > ScreenDefinition.MaxTextCharacters) throw new InvalidOperationException("Превышен бюджет текста изменений.");
                updated = node with { Text = text };
            }
            else if (!extra && change.Text is null && change.Value is { } value && change.Kind is "visible" or "enabled")
                updated = change.Kind == "visible" ? node with { Visible = value } : node with { Enabled = value };
            else throw new InvalidOperationException("Недопустимая команда экрана.");
            if (updated != node) current = new(current.Id, current.Nodes.Replace(node, updated), current.RetiredNodeIds);
        }
        // Still advance protocol versions for successful no-ops, but preserve view identity.
        if (close && service is not null) throw new InvalidOperationException("Нельзя закрыть экран и запросить сервис одним пакетом.");
        if (triggers.Count != 0 && (close || service is not null)) throw new InvalidOperationException("Триггеры нельзя совмещать с закрытием или сервисом в одном пакете.");
        return new(current.Nodes.SequenceEqual(definition.Nodes) && current.RetiredNodeIds.SetEquals(definition.RetiredNodeIds) ? definition : current, close, service) { Triggers = triggers.ToImmutable(), State = state };
    }
}
