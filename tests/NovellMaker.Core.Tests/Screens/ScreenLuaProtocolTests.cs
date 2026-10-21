using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenLuaProtocolTests
{
    internal static ScreenDefinition Definition()
    {
        var root = Guid.NewGuid();
        return new(Guid.NewGuid(), [new(root, null, ScreenNodeKind.Panel, new(0, 0, 100, 100)),
            new(Guid.NewGuid(), root, ScreenNodeKind.Button, new(10, 10, 50, 30), "first"),
            new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(10, 50, 80, 20), "label")]);
    }
    internal static ScreenLuaRequest Request(string kind = "event") => new(ScreenLuaProtocol.Version, kind,
        new(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), 0), Guid.NewGuid(), [], null,
        ImmutableDictionary<string, Guid>.Empty, new("update"), ImmutableDictionary<string, string>.Empty);
    internal static ScreenLuaResponse Reply(ScreenLuaRequest request, params ScreenChange[] changes) =>
        new(ScreenLuaProtocol.Version, request.Kind switch { "handshake" => "ready", "close" => "closed", _ => "batch" }, request.Stamp, null, [.. changes]);

    [TestMethod]
    public void ChangesArePreparedAtomicallyAndKeepAllStableIds()
    {
        var before = Definition(); var button = before.Nodes[1];
        var prepared = ScreenLuaProtocol.Prepare(before, [new("text", button.Id, "новое", null),
            new("visible", button.Id, null, false), new("enabled", button.Id, null, false), new("close", null, null, null)]);
        Assert.AreEqual("first", before.Nodes[1].Text);
        Assert.AreEqual("новое", prepared.Definition.Nodes[1].Text);
        Assert.IsFalse(prepared.Definition.Nodes[1].Visible); Assert.IsFalse(prepared.Definition.Nodes[1].Enabled);
        Assert.AreEqual(before.Id, prepared.Definition.Id);
        Assert.AreSequenceEqual(before.Nodes.Select(n => n.Id), prepared.Definition.Nodes.Select(n => n.Id));
        Assert.IsTrue(prepared.Close);
        Assert.AreSame(before, ScreenLuaProtocol.Prepare(before, []).Definition);
        var enabled = ScreenLuaProtocol.Prepare(prepared.Definition, [new("enabled", button.Id, null, true), new("visible", button.Id, null, true)]);
        Assert.IsTrue(enabled.Definition.Nodes[1].Enabled); Assert.IsTrue(enabled.Definition.Nodes[1].Visible);
    }

    [TestMethod]
    [DataRow("default")][DataRow("count")][DataRow("null")][DataRow("foreign")][DataRow("no-node")]
    [DataRow("unknown")][DataRow("missing-text")][DataRow("large-text")][DataRow("text-value")]
    [DataRow("missing-value")][DataRow("value-text")][DataRow("after-close")][DataRow("bad-close")]
    [DataRow("batch-text")][DataRow("resource-text")]
    public void InvalidBatchRejectsEveryCommandWithoutChangingDefinition(string mutation)
    {
        var before = Definition(); var id = before.Nodes[1].Id;
        ImmutableArray<ScreenChange> changes = mutation switch
        {
            "default" => default,
            "count" => [.. Enumerable.Repeat(new ScreenChange("enabled", id, null, true), 257)],
            "null" => [null!],
            "foreign" => [new("text", Guid.NewGuid(), "x", null)],
            "no-node" => [new("text", null, "x", null)],
            "unknown" => [new("jump", id, null, null)],
            "missing-text" => [new("text", id, null, null)],
            "large-text" => [new("text", id, new('a', 8193), null)],
            "text-value" => [new("text", id, "x", true)],
            "missing-value" => [new("enabled", id, null, null)],
            "value-text" => [new("visible", id, "x", true)],
            "after-close" => [new("close", null, null, null), new("text", id, "x", null)],
            "bad-close" => [new("close", id, null, null)],
            "batch-text" => [.. Enumerable.Repeat(new ScreenChange("text", id, new('a', 8192), null), 17)],
            _ => []
        };
        if (mutation == "resource-text")
        {
            var root = before.Nodes[0];
            before = new(before.Id, [root, .. Enumerable.Range(0, 16).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id,
                ScreenNodeKind.Text, new(0, 0, 1, 1), new('a', 8192)))]);
            changes = [new("text", root.Id, "x", null)];
        }
        var unchanged = before;
        Assert.Throws<Exception>(() => ScreenLuaProtocol.Prepare(before, changes));
        Assert.AreSame(unchanged, before);
    }

    [TestMethod]
    public void ExactCommandAndPayloadLimitsAreAccepted()
    {
        var definition = Definition(); var id = definition.Nodes[1].Id;
        _ = ScreenLuaProtocol.Prepare(definition, [.. Enumerable.Repeat(new ScreenChange("enabled", id, null, true), 256)]);
        _ = ScreenLuaProtocol.Prepare(definition, [.. Enumerable.Repeat(new ScreenChange("text", id, new('x', 8192), null), 16)]);
    }

    [TestMethod]
    [DataRow("version")][DataRow("request")][DataRow("session")][DataRow("generation")][DataRow("owner")]
    [DataRow("instance")][DataRow("state")][DataRow("kind")][DataRow("default")][DataRow("count")]
    [DataRow("null")][DataRow("empty-error")][DataRow("large-error")][DataRow("error-changes")][DataRow("wrong-error-kind")]
    public void ResponseMustMatchEveryIdentityFieldAndStrictShape(string mutation)
    {
        var request = Request(); var response = Reply(request);
        response = mutation switch
        {
            "version" => response with { Protocol = 1 }, "request" => response with { Stamp = response.Stamp with { RequestId = Guid.NewGuid() } },
            "session" => response with { Stamp = response.Stamp with { SessionId = Guid.NewGuid() } },
            "generation" => response with { Stamp = response.Stamp with { Generation = 2 } },
            "owner" => response with { Stamp = response.Stamp with { OwnerId = Guid.NewGuid() } },
            "instance" => response with { Stamp = response.Stamp with { InstanceId = Guid.NewGuid() } },
            "state" => response with { Stamp = response.Stamp with { Version = 1 } }, "kind" => response with { Kind = "ready" },
            "default" => response with { Changes = default },
            "count" => response with { Changes = [.. Enumerable.Repeat(new ScreenChange("close", null, null, null), 257)] },
            "null" => response with { Changes = [null!] }, "empty-error" => response with { Kind = "error", Error = "" },
            "large-error" => response with { Kind = "error", Error = new('x', 4097) },
            "error-changes" => response with { Kind = "error", Error = "error", Changes = [new("close", null, null, null)] },
            _ => response with { Error = "error" }
        };
        Assert.ThrowsExactly<IOException>(() => ScreenLuaConnection.Validate(request, response));
    }

    [TestMethod]
    public void HandshakeAndCloseHaveNoChangesAndErrorHasNoPartialBatch()
    {
        foreach (var kind in new[] { "handshake", "close", "event" })
        {
            var request = Request(kind); var response = Reply(request);
            ScreenLuaConnection.Validate(request, response);
            ScreenLuaConnection.Validate(request, response with { Kind = "error", Error = new('x', 4096) });
            if (kind != "event")
                Assert.ThrowsExactly<IOException>(() => ScreenLuaConnection.Validate(request, response with { Changes = [new("close", null, null, null)] }));
        }
    }

    [TestMethod]
    public async Task FramedWireRoundTripsMultipleMessagesAndRejectsTruncationOrOversize()
    {
        using var stream = new MemoryStream(); var request = Request();
        await ScreenLuaProtocol.WriteAsync(stream, request, CancellationToken.None);
        await ScreenLuaProtocol.WriteAsync(stream, Reply(request), CancellationToken.None);
        stream.Position = 0;
        Assert.AreEqual(request.Stamp, (await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(stream, CancellationToken.None)).Stamp);
        Assert.AreEqual(request.Stamp, (await ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(stream, CancellationToken.None)).Stamp);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(stream, CancellationToken.None));
        foreach (var count in new[] { -1, 0, 1024 * 1024 + 1 })
        {
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, count);
            await Assert.ThrowsExactlyAsync<IOException>(() => ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(new MemoryStream(header), CancellationToken.None));
        }
        var huge = request with { Source = new('a', 1024 * 1024) };
        await Assert.ThrowsExactlyAsync<IOException>(() => ScreenLuaProtocol.WriteAsync(new MemoryStream(), huge, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(stream, cancellation.Token));
    }

    [TestMethod]
    [DataRow("null")][DataRow("{}")][DataRow("{\"Protocol\":1,\"Protocol\":1}")]
    public async Task StrictWireRejectsMissingDuplicateOrNullMessages(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json); var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        using var stream = new MemoryStream([.. header, .. bytes]);
        await Assert.ThrowsAsync<Exception>(() => ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(stream, CancellationToken.None));
        var request = Request(); var valid = JsonSerializer.Serialize(Reply(request), ScreenLuaProtocol.Json);
        var unknown = valid.Insert(1, "\"Extra\":0,");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<ScreenLuaResponse>(unknown, ScreenLuaProtocol.Json));
    }

    [TestMethod]
    public void EventValidationAllowsOnlyOwnedBoundedEvents()
    {
        var definition = Definition();
        foreach (var kind in new[] { "click", "move", "hover", "update" })
            ScreenLuaProtocol.ValidateEvent(definition, new(kind, definition.Nodes[1].Id, -100000, 100000, 1));
        ScreenLuaProtocol.ValidateEvent(definition, new("update"));
        foreach (var value in new ScreenLuaEvent[] { new("unknown"), new("click"), new("click", definition.Nodes[2].Id),
            new("move", Guid.NewGuid()), new("move", X: double.NaN), new("move", Y: double.PositiveInfinity),
            new("move", X: 100001), new("move", Y: -100001), new("update", Seconds: -1),
            new("update", Seconds: 1.01), new("update", Seconds: double.NaN) })
            Assert.ThrowsExactly<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(definition, value));
    }
}
