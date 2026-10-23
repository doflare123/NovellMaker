using System.Collections.Immutable;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Triggers;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class TriggerTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;

    [TestMethod]
    [DataRow(null)] [DataRow("custom payload")]
    public async Task RealWorkerQueuesNamedEventInOriginWithCapturedSnapshotAndNoAutomaticPersistence(string? payload)
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        var literal = payload is null ? "nil" : "'custom payload'";
        session.Attach(origin, "return {on_load=function() ui.trigger('quest.step'," + literal + ") end,on_event=function(e) " +
            "if e.kind=='trigger' then ui.set_text(refs.label,e.trigger_id..':'..(e.trigger_data or 'nil')..':'..ui.get('chapter')) end end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", origin.Definition.Nodes[2].Id), Empty.Add("chapter", "one"));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.IsEmpty(session.DrainServices());
        Assert.AreEqual("quest.step:" + (payload ?? "nil") + ":one", origin.Definition.Nodes[2].Text);
        Assert.AreEqual(2, session.AppliedBatches);
    }

    [TestMethod]
    public async Task EventsRunInEmissionOrderAndCanStartASeparatePersistentRequest()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(origin, """
            local seen=''
            return {on_load=function() ui.trigger('a'); ui.trigger('b'); ui.trigger('c') end,
              on_event=function(e)
                if e.kind=='trigger' then
                  seen=seen..e.trigger_id
                  ui.set_text(refs.label,seen)
                  if e.trigger_id=='c' then ui.set_persistent('quest_state',seen) end
                end
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", origin.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("abc", origin.Definition.Nodes[2].Text);
        var command = session.DrainServices().Single().Command;
        Assert.AreEqual("quest_state", command.Key); Assert.AreEqual("abc", command.DataValue);
    }

    [TestMethod]
    [DataRow("ui.trigger('a'); ui.close()")]
    [DataRow("ui.trigger('a'); ui.get_persistent('key')")]
    [DataRow("ui.trigger('a'); ui.set_text('foreign','bad')")]
    [DataRow("ui.trigger('../bad')")]
    [DataRow("ui.trigger('a',false)")]
    [DataRow("ui.trigger('a',1)")]
    [DataRow("ui.get_achievement('a')")]
    [DataRow("ui.unlock_achievement('a')")]
    public async Task BadOrMixedBatchHasNoEventsServicesOrVisualEffects(string code)
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var before = origin.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(origin, "return {on_load=function() ui.set_text(refs.label,'changed'); " + code + " end,on_event=function(e) ui.set_text(refs.label,'triggered') end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", origin.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.IsEmpty(session.DrainServices()); Assert.AreSame(before, origin.Definition);
    }

    [TestMethod]
    [DataRow(16, true)] [DataRow(17, false)]
    public async Task PerBatchLimitIsAtomic(int count, bool accepted)
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var before = origin.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(origin, "return {on_load=function() ui.set_text(refs.label,'changed'); for i=1," + count +
            " do ui.trigger('signal') end end,on_event=function(e) end}", ImmutableDictionary<string, Guid>.Empty.Add("label", origin.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        if (accepted) { Assert.IsEmpty(session.Diagnostics); Assert.AreEqual(count + 1, session.AppliedBatches); }
        else { Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(before, origin.Definition); }
    }

    [TestMethod]
    public async Task SelfEmittingChainStopsAtTheHostBudget()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(origin, "return {on_load=function() ui.trigger('again') end,on_event=function(e) ui.trigger('again') end}");
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsTrue(session.Diagnostics.Any(s => s.Contains("глубины", StringComparison.Ordinal)));
        Assert.AreEqual(AuthorTrigger.MaximumChainDepth, session.AppliedBatches);
        Assert.IsFalse(session.HasPending); Assert.IsEmpty(session.DrainServices());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ClosedOriginOrResetDiscardsAPreparedTriggerBatch(bool reset)
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var before = origin.Definition;
        var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(origin, "return {on_event=function(e) end}"); session.Poll(); var call = await connection.Next();
        if (reset) session.Reset(); else stack.Close(origin);
        call.Reply(new("text", before.Nodes[2].Id, "changed", null), new("trigger", null, null, null) { Trigger = new("signal") });
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(before, origin.Definition); Assert.AreEqual(0, session.AppliedBatches); Assert.IsEmpty(session.DrainServices());
    }

    [TestMethod]
    public async Task FullHostQueueRejectsWholeTriggerBatchBeforeVisualCommit()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var before = origin.Definition;
        var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(origin, "return {on_event=function(e) end}"); session.Poll(); var call = await connection.Next();
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents; i++)
            Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(origin, new("update"), Empty));
        call.Reply(new("text", before.Nodes[2].Id, "changed", null), new("trigger", null, null, null) { Trigger = new("signal") });
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(before, origin.Definition); Assert.AreEqual(0, session.AppliedBatches);
        Assert.IsTrue(session.Diagnostics.Any(s => s.Contains("очереди", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(null)] [DataRow("author payload")]
    public void TriggerProjectionIsASeparateTypedEventInsideTheVm(string? payload)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), """
            return {on_event=function(e)
              if e.kind=='trigger' then ui.set_text(refs.label,e.trigger_id..':'..(e.trigger_data or 'nil')) end
            end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id));
        var batch = ScreenLuaProtocol.Prepare(definition, vm.Handle(new("trigger") { Trigger = new("author.event", payload) }, Empty));
        Assert.AreEqual("author.event:" + (payload ?? "nil"), batch.Definition.Nodes[2].Text);
    }

    [TestMethod]
    public void TriggerShapesKeysAndPayloadsAreStrictOnBothSidesOfTheWire()
    {
        var definition = ScreenLuaProtocolTests.Definition();
        foreach (var id in new[] { "a", "quest.step-1", new string('z', 64) })
            new AuthorTrigger(id, new string('x', 1024)).Validate();
        foreach (var trigger in new[] { new AuthorTrigger(null!), new(""), new("A"), new(new string('z', 65)), new("a", new string('x', 1025)) })
            Assert.Throws<ArgumentException>(trigger.Validate);
        var value = new ScreenLuaEvent("trigger") { Trigger = new("signal") }; ScreenLuaProtocol.ValidateEvent(definition, value);
        foreach (var invalid in new[] { value with { Trigger = null }, value with { NodeId = definition.Nodes[2].Id }, value with { X = 1 },
            value with { Y = 1 }, value with { Seconds = 1 }, value with { Kind = "update" }, value with { Kind = "service_result" },
            value with { Result = new(Guid.NewGuid(), GameServiceAction.MainMenu, true, "", null, []) } })
            Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(definition, invalid));
        var change = new ScreenChange("trigger", null, null, null) { Trigger = new("signal") };
        foreach (var invalid in new[] { change with { Kind = "text" }, change with { Trigger = null }, change with { Text = "extra" }, change with { Value = true },
            change with { NodeId = definition.Nodes[0].Id }, change with { Patch = new() }, change with { ParentId = definition.Nodes[0].Id },
            change with { Ids = ImmutableDictionary<Guid, Guid>.Empty }, change with { Service = new(Guid.NewGuid(), GameServiceAction.MainMenu) } })
            Assert.Throws<Exception>(() => ScreenLuaProtocol.Prepare(definition, [invalid]));
    }
}
