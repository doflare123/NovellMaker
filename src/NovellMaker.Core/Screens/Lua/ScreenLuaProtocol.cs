using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Screens.Lua;

public sealed record ScreenLuaStamp(Guid RequestId, Guid SessionId, long Generation, Guid OwnerId, Guid InstanceId, long Version);
public sealed record ScreenLuaEvent(string Kind, Guid? NodeId = null, double X = 0, double Y = 0, double Seconds = 0);
public sealed record ScreenChange(string Kind, Guid? NodeId, string? Text, bool? Value);
internal sealed record ScreenLuaRequest(int Protocol, string Kind, ScreenLuaStamp Stamp, Guid ResourceId,
    ImmutableArray<ScreenNode> Nodes, string? Source, ImmutableDictionary<string, Guid> References,
    ScreenLuaEvent? Event, ImmutableDictionary<string, string> Snapshot);
internal sealed record ScreenLuaResponse(int Protocol, string Kind, ScreenLuaStamp Stamp, string? Error, ImmutableArray<ScreenChange> Changes);
internal sealed record ScreenPreparedBatch(ScreenDefinition Definition, bool Close);

internal static class ScreenLuaProtocol
{
    internal const int Version = 1;
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

    internal static void ValidateEvent(ScreenDefinition definition, ScreenLuaEvent value)
    {
        if (value.Kind is not ("click" or "move" or "hover" or "update") ||
            !double.IsFinite(value.X) || !double.IsFinite(value.Y) || Math.Abs(value.X) > 100000 || Math.Abs(value.Y) > 100000 ||
            !double.IsFinite(value.Seconds) || value.Seconds is < 0 or > 1 ||
            value.NodeId is { } node && !definition.Nodes.Any(n => n.Id == node) ||
            value.Kind == "click" && (value.NodeId is null || !definition.Buttons.Any(n => n.Id == value.NodeId)))
            throw new ArgumentException("Недопустимое событие экрана.");
    }

    internal static ScreenPreparedBatch Prepare(ScreenDefinition definition, ImmutableArray<ScreenChange> changes)
    {
        if (changes.IsDefault || changes.Length > LuaActionScript.MaximumCommands)
            throw new InvalidOperationException("Превышен бюджет изменений экрана.");
        if (changes.IsEmpty) return new(definition, false);
        var nodes = definition.Nodes.ToDictionary(n => n.Id);
        var close = false;
        var characters = 0;
        foreach (var change in changes)
        {
            if (change is null || close) throw new InvalidOperationException("Некорректный пакет экрана.");
            if (change.Kind == "close" && change.NodeId is null && change.Text is null && change.Value is null)
            { close = true; continue; }
            if (change.NodeId is not { } id || !nodes.TryGetValue(id, out var node))
                throw new InvalidOperationException("Чужой узел экрана.");
            if (change.Kind == "text" && change.Value is null && change.Text is { Length: <= 8192 } text)
            {
                characters += text.Length;
                if (characters > ScreenDefinition.MaxTextCharacters) throw new InvalidOperationException("Превышен бюджет текста изменений.");
                nodes[id] = node with { Text = text };
            }
            else if (change.Text is null && change.Value is { } value && change.Kind is "visible" or "enabled")
                nodes[id] = change.Kind == "visible" ? node with { Visible = value } : node with { Enabled = value };
            else throw new InvalidOperationException("Недопустимая команда экрана.");
        }
        return new(new(definition.Id, [.. definition.Nodes.Select(n => nodes[n.Id])]), close);
    }
}
