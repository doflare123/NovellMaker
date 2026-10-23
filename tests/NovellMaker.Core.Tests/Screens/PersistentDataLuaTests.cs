using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class PersistentDataLuaTests
{
    [TestMethod]
    [DataRow(null)] [DataRow("arbitrary")]
    public async Task PersistentReadAndDeleteRoundTripThroughRealWorkerWithCorrelatedStatus(string? value)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            local request
            return {on_load=function() request=ui.get_persistent('chapter_1.finish') end,
              on_event=function(e)
                if e.kind=='service_result' and e.request_id==request and e.key=='chapter_1.finish' then
                  ui.set_text(refs.label,e.value or 'missing')
                  ui.set_persistent(e.key)
                end
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); var command = session.DrainServices().Single().Command;
        Assert.AreEqual(GameServiceAction.GetPersistent, command.Action); Assert.AreEqual("chapter_1.finish", command.Key);
        var result = new GameServiceResult(command.RequestId, command.Action, true, "", null, []) { Key = command.Key, DataValue = value, ProfileSaved = true };
        session.Enqueue(instance, new("service_result") { Result = result }, ImmutableDictionary<string, string>.Empty);
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual(value ?? "missing", instance.Definition.Nodes[2].Text);
        command = session.DrainServices().Single().Command;
        Assert.AreEqual(GameServiceAction.SetPersistent, command.Action); Assert.AreEqual(result.Key, command.Key);
        Assert.AreNotEqual(result.RequestId, command.RequestId);
    }

    [TestMethod]
    [DataRow("ui.set_persistent('../bad')")]
    [DataRow("ui.get_persistent('')")]
    [DataRow("ui.set_persistent(1)")]
    [DataRow("ui.set_persistent('a',true)")]
    [DataRow("ui.set_persistent('a',1)")]
    [DataRow("ui.set_persistent('a'); ui.close()")]
    [DataRow("ui.set_persistent('a'); ui.get_settings()")]
    [DataRow("ui.set_persistent('a'); ui.set_text('foreign','bad')")]
    public async Task FailedLuaBatchCannotPublishAWriteOrPartialVisualChange(string operation)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() ui.set_text(refs.label,'changed'); " + operation + " end,on_event=function(e) end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.IsEmpty(session.DrainServices()); Assert.AreSame(before, instance.Definition);
    }

    [TestMethod]
    [DataRow(null)] [DataRow("arbitrary")]
    public void PersistentResultProjectionIsTypedInsideTheVm(string? value)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), """
            return {on_event=function(e)
              if e.key=='first' and e.operation=='GetPersistent' then
                ui.set_text(refs.label,e.value or 'missing')
              end
            end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id));
        var result = new GameServiceResult(Guid.NewGuid(), GameServiceAction.GetPersistent, true, "", null, []) { Key = "first", DataValue = value, ProfileSaved = true };
        var batch = ScreenLuaProtocol.Prepare(definition, vm.Handle(new("service_result") { Result = result }, ImmutableDictionary<string, string>.Empty));
        Assert.AreEqual(value ?? "missing", batch.Definition.Nodes[2].Text);
    }

    [TestMethod]
    public void PersistentCommandsAndResultsRejectSmuggledOrMissingData()
    {
        var command = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.SetPersistent) { Key = "a" }; command.Validate();
        foreach (var invalid in new[] { command with { Key = null }, command with { Key = new string('a', 65) }, command with { DataValue = new string('x', 1025) },
            command with { SlotId = "slot-1" }, command with { Title = "title" }, command with { SceneId = Guid.NewGuid() },
            command with { Setting = new("fullscreen", null, true) }, command with { Action = GameServiceAction.GetSettings },
            command with { Action = GameServiceAction.NewGame } }) Assert.Throws<ArgumentException>(invalid.Validate);
        var result = new GameServiceResult(command.RequestId, command.Action, true, "", null, []) { Key = "a", DataValue = "arbitrary", ProfileSaved = true }; result.Validate();
        (result with { Success = false, DataValue = null }).Validate();
        foreach (var invalid in new[] { result with { ProfileSaved = null }, result with { Key = null }, result with { Key = "A" },
            result with { DataValue = new string('x', 1025) }, result with { SlotId = "slot-1" }, result with { Settings = PlayerSettings.Defaults(new()) },
            result with { Action = GameServiceAction.ListSlots }, result with { Action = GameServiceAction.GetSettings } }) Assert.Throws<ArgumentException>(invalid.Validate);
    }
}
