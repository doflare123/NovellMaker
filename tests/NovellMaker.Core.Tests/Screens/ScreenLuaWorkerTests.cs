using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
[DoNotParallelize]
public sealed class ScreenLuaWorkerTests
{
    // Trusted, fixed fixtures exercise worker logic in isolation. Hostile loops run only in real children below.
    private const string Empty = "return { on_event = function(event) end }";
    private static (ScreenLuaWorkerService Service, ScreenLuaRequest Load) Worker(string source = Empty)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        var hello = ScreenLuaProtocolTests.Request("handshake");
        hello = hello with { Stamp = hello.Stamp with { OwnerId = Guid.Empty, InstanceId = Guid.Empty }, ResourceId = Guid.Empty, Event = null };
        var service = new ScreenLuaWorkerService();
        Assert.IsNull(service.Process(hello).Error);
        var request = hello with { Kind = "load", Stamp = hello.Stamp with { RequestId = Guid.NewGuid(), InstanceId = Guid.NewGuid(), OwnerId = Guid.NewGuid() },
            ResourceId = definition.Id, Nodes = definition.Nodes, Source = source,
            References = ImmutableDictionary<string, Guid>.Empty.Add("button", definition.Nodes[1].Id).Add("label", definition.Nodes[2].Id) };
        return (service, request);
    }
    private static ScreenLuaRequest Event(ScreenLuaRequest load, long version = 1) => load with
    {
        Kind = "event", Stamp = load.Stamp with { RequestId = Guid.NewGuid(), Version = version }, Source = null, Nodes = [],
        References = ImmutableDictionary<string, Guid>.Empty, Event = new("click", load.Nodes[1].Id),
    };

    [TestMethod]
    public void PersistentClosuresReceiveFreshSnapshotAndEachInstanceHasItsOwnVm()
    {
        var (service, load) = Worker("""
            local count = 0
            return {
              on_load = function() ui.set_text(refs.label, ui.get("value")) end,
              on_event = function(event)
                count = count + 1
                ui.set_text(refs.label, event.kind .. ':' .. count .. ':' .. ui.get("value"))
              end
            }
            """);
        load = load with { Snapshot = ImmutableDictionary<string, string>.Empty.Add("value", "initial") };
        var loaded = service.Process(load); Assert.IsNull(loaded.Error); Assert.AreEqual("initial", loaded.Changes.Single().Text);
        var click = Event(load) with { Snapshot = ImmutableDictionary<string, string>.Empty.Add("value", "fresh") };
        Assert.AreEqual("click:1:fresh", service.Process(click).Changes.Single().Text);
        click = click with { Stamp = click.Stamp with { RequestId = Guid.NewGuid(), Version = 2 } };
        Assert.AreEqual("click:2:fresh", service.Process(click).Changes.Single().Text);
        var other = load with { Stamp = load.Stamp with { InstanceId = Guid.NewGuid(), RequestId = Guid.NewGuid() } };
        Assert.IsNull(service.Process(other).Error);
        Assert.AreEqual("click:1:fresh", service.Process(Event(other) with { Snapshot = click.Snapshot }).Changes.Single().Text);
        var close = click with { Kind = "close", Stamp = click.Stamp with { Version = 3 }, Event = null, Snapshot = ImmutableDictionary<string, string>.Empty };
        Assert.AreEqual("closed", service.Process(close).Kind);
        Assert.IsNotNull(service.Process(click with { Stamp = click.Stamp with { Version = 3 } }).Error);
    }

    [TestMethod]
    public void ApiIsOwnerScopedAndHasNoScenarioLibrariesOrOperatingSystemAccess()
    {
        var (service, load) = Worker("""
            return { on_event = function(event)
              if os ~= nil or io ~= nil or require ~= nil or load ~= nil or game ~= nil or coroutine ~= nil or debug ~= nil then
                ui.set_text(refs.label, "unsafe")
              else
                ui.set_text(refs.label, "isolated")
              end
              if ui.get("missing") == nil then ui.set_visible(refs.label, true) end
              ui.set_enabled(refs.button, false)
              ui.close()
            end }
            """);
        Assert.IsNull(service.Process(load).Error);
        var result = service.Process(Event(load));
        Assert.IsNull(result.Error); Assert.AreEqual("isolated", result.Changes[0].Text);
        Assert.AreEqual(4, result.Changes.Length); Assert.AreEqual("close", result.Changes[^1].Kind);
    }

    [TestMethod]
    [DataRow("return 1")]
    [DataRow("return {}")]
    [DataRow("return {on_event=1}")]
    [DataRow("return {on_event=function() end,on_load=1}")]
    [DataRow("ui.close(); return {on_event=function() end}")]
    [DataRow("if then")]
    [DataRow("return {on_event=function() end,on_load=function() ui.set_text(refs.label, false) end}")]
    public void InvalidLoadReturnsErrorAndNoPartialChanges(string source)
    {
        var (service, load) = Worker(source); var result = service.Process(load);
        Assert.AreEqual("error", result.Kind); Assert.IsNotNull(result.Error); Assert.IsTrue(result.Changes.IsEmpty);
        Assert.IsNotNull(service.Process(Event(load)).Error);
    }

    [TestMethod]
    public void FailedHandlerHasNoPartialBatchAndItsMutatedVmIsDiscarded()
    {
        var (service, load) = Worker("""
            return {on_event=function()
              ui.set_text(refs.label, "partial")
              ui.set_visible("00000000-0000-0000-0000-000000000001", false)
            end}
            """);
        Assert.IsNull(service.Process(load).Error);
        var result = service.Process(Event(load)); Assert.IsNotNull(result.Error); Assert.IsTrue(result.Changes.IsEmpty);
        var again = service.Process(Event(load)); StringAssert.Contains(again.Error!, "Устаревшие");
    }

    [TestMethod]
    [DataRow("protocol")][DataRow("request")][DataRow("session")][DataRow("generation")][DataRow("negative-version")]
    [DataRow("default-nodes")][DataRow("refs")][DataRow("snapshot")][DataRow("owner")][DataRow("instance")]
    [DataRow("resource")][DataRow("load-version")][DataRow("load-source")][DataRow("load-event")][DataRow("foreign-ref")]
    public void WorkerRejectsMalformedOrForeignRequestWithoutExecutingUserCode(string mutation)
    {
        var (service, load) = Worker();
        var request = mutation switch
        {
            "protocol" => load with { Protocol = 2 }, "request" => load with { Stamp = load.Stamp with { RequestId = Guid.Empty } },
            "session" => load with { Stamp = load.Stamp with { SessionId = Guid.NewGuid() } },
            "generation" => load with { Stamp = load.Stamp with { Generation = 2 } },
            "negative-version" => load with { Stamp = load.Stamp with { Version = -1 } },
            "default-nodes" => load with { Nodes = default },
            "refs" => load with { References = ImmutableDictionary<string, Guid>.Empty.Add("bad-name", Guid.NewGuid()) },
            "snapshot" => load with { Snapshot = ImmutableDictionary<string, string>.Empty.Add("", "value") },
            "owner" => load with { Stamp = load.Stamp with { OwnerId = Guid.Empty } },
            "instance" => load with { Stamp = load.Stamp with { InstanceId = Guid.Empty } },
            "resource" => load with { ResourceId = Guid.Empty },
            "load-version" => load with { Stamp = load.Stamp with { Version = 1 } },
            "load-source" => load with { Source = null }, "load-event" => load with { Event = new("update") },
            _ => load with { References = ImmutableDictionary<string, Guid>.Empty.Add("other", Guid.NewGuid()) }
        };
        var response = service.Process(request); Assert.IsNotNull(response.Error); Assert.IsTrue(response.Changes.IsEmpty);
    }

    [TestMethod]
    [DataRow("owner")][DataRow("version")][DataRow("resource")][DataRow("nodes")][DataRow("source")]
    [DataRow("refs")][DataRow("kind")][DataRow("event")][DataRow("foreign-node")]
    public void RegisteredInstanceRequiresCurrentOwnerVersionAndEventShape(string mutation)
    {
        var (service, load) = Worker(); Assert.IsNull(service.Process(load).Error);
        Assert.IsNotNull(service.Process(load).Error, "Duplicate load must fail.");
        var request = Event(load);
        request = mutation switch
        {
            "owner" => request with { Stamp = request.Stamp with { OwnerId = Guid.NewGuid() } },
            "version" => request with { Stamp = request.Stamp with { Version = 0 } },
            "resource" => request with { ResourceId = Guid.NewGuid() }, "nodes" => request with { Nodes = load.Nodes },
            "source" => request with { Source = Empty }, "refs" => request with { References = load.References },
            "kind" => request with { Kind = "run" }, "event" => request with { Event = null },
            _ => request with { Event = new("click", Guid.NewGuid()) }
        };
        Assert.IsNotNull(service.Process(request).Error);
        Assert.IsNull(service.Process(Event(load)).Error, "Rejected metadata must not execute or delete a valid VM.");
    }

    [TestMethod]
    public void HandshakeIsVersionedUniqueAndRequiredBeforeLoad()
    {
        var (_, load) = Worker(); var service = new ScreenLuaWorkerService();
        Assert.IsNotNull(service.Process(load).Error);
        var hello = load with { Kind = "handshake", Stamp = load.Stamp with { OwnerId = Guid.Empty, InstanceId = Guid.Empty },
            ResourceId = Guid.Empty, Nodes = [], Source = null, References = ImmutableDictionary<string, Guid>.Empty };
        foreach (var invalid in new[] { hello with { Stamp = hello.Stamp with { SessionId = Guid.Empty } },
            hello with { Stamp = hello.Stamp with { Generation = 0 } }, hello with { Stamp = hello.Stamp with { OwnerId = Guid.NewGuid() } },
            hello with { Stamp = hello.Stamp with { InstanceId = Guid.NewGuid() } }, hello with { Stamp = hello.Stamp with { Version = 1 } },
            hello with { ResourceId = Guid.NewGuid() }, hello with { Nodes = load.Nodes }, hello with { Source = Empty },
            hello with { References = load.References }, hello with { Event = new("update") },
            hello with { Snapshot = ImmutableDictionary<string, string>.Empty.Add("x", "y") } })
            Assert.IsNotNull(service.Process(invalid).Error);
        Assert.AreEqual("ready", service.Process(hello).Kind);
        Assert.IsNotNull(service.Process(hello).Error);
        Assert.IsNull(service.Process(load).Error);
    }

    [TestMethod]
    public void WorkerEnforcesInstanceAndAggregateNodeBudgets()
    {
        var (service, load) = Worker();
        for (var i = 0; i < 32; i++)
            Assert.IsNull(service.Process(load with { Stamp = load.Stamp with { InstanceId = Guid.NewGuid(), RequestId = Guid.NewGuid() } }).Error);
        Assert.IsNotNull(service.Process(load).Error);
        (service, load) = Worker();
        var root = load.Nodes[0];
        load = load with { Nodes = [root, .. Enumerable.Range(0, 1023).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(0, 0, 1, 1)))],
            References = ImmutableDictionary<string, Guid>.Empty };
        for (var i = 0; i < 4; i++)
            Assert.IsNull(service.Process(load with { Stamp = load.Stamp with { InstanceId = Guid.NewGuid(), RequestId = Guid.NewGuid() } }).Error);
        Assert.IsNotNull(service.Process(load).Error);
    }

    [TestMethod]
    public async Task RealSessionPersistsHandlerLocalsAndV1CanRunAlongsideIt()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            local count = 0
            return { on_event = function(e) count = count + 1; ui.set_text(refs.label, 'count=' .. count) end }
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsTrue(session.Diagnostics.IsEmpty);
        Assert.IsNull((await LuaWorkerClient.InvokeAsync(new("syntax", "return 1", "v1.lua"))).Error);
        for (var i = 1; i <= 3; i++)
        {
            session.Enqueue(instance, new("update", Seconds: 0.1)); await ScreenLuaSessionTests.Drain(session);
            Assert.AreEqual($"count={i}", instance.Definition.Nodes[2].Text);
        }
        stack.Clear(); session.Poll(); await ScreenLuaSessionTests.Drain(session);
        Assert.IsTrue(session.Diagnostics.IsEmpty);
    }

    [TestMethod]
    [DataRow("return {on_event=function() while true do end end}")]
    [DataRow("return {on_event=function() local f; f=function() return f() end; f() end}")]
    [DataRow("return {on_event=function() for i=1,257 do ui.close() end end}")]
    [DataRow("return {on_event=function() io.open('outside.txt','w') end}")]
    [DataRow("return {on_event=function() ui.close(); ui.close() end}")]
    public async Task RealWorkerStopsHostileHandlerAndLeavesEngineCloseUsable(string source)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source); await ScreenLuaSessionTests.Drain(session);
        Assert.IsTrue(session.Diagnostics.IsEmpty);
        session.Enqueue(instance, new("update")); await ScreenLuaSessionTests.Drain(session);
        Assert.IsFalse(session.IsAvailable(instance)); Assert.IsFalse(session.Diagnostics.IsEmpty);
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(1L, instance.Version);
        Assert.IsTrue(stack.Route(new(ScreenInputKind.Back)).Consumed);
        await session.DisposeAsync();
    }
}
