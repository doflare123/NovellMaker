using System.Collections.Immutable;
using System.Text.Json;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class LuaDiagnosticsTests
{
    private static readonly ImmutableDictionary<string, Guid> Refs = ImmutableDictionary<string, Guid>.Empty;
    private static readonly ImmutableDictionary<string, string> Data = ImmutableDictionary<string, string>.Empty;
    private static ScreenLuaVm Vm(string source) => new(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(), source, Refs);

    [TestMethod]
    public void LogUsesRealCallingLineAndDoesNotProduceCommands()
    {
        var vm = Vm("return {on_load=function()\n ui.log('info','Привет')\nend,on_event=function() end}");
        Assert.IsEmpty(vm.Handle(null, Data));
        var message = vm.Log.Messages.Single();
        Assert.AreEqual("info", message.Level); Assert.AreEqual("Привет", message.Message); Assert.AreEqual(2, message.Line); Assert.IsNull(message.ScriptId);
    }

    [TestMethod]
    [DataRow("ui.log()")][DataRow("ui.log('info')")][DataRow("ui.log('info','ok',3)")]
    [DataRow("ui.log(1,'ok')")][DataRow("ui.log('info',42)")][DataRow("ui.log('warn','ok')")]
    [DataRow("ui.log('INFO','ok')")][DataRow("ui.log('info',{})")]
    public void InvalidLogArgumentsFailExplicitly(string code)
        => Assert.Throws<InterpreterException>(() => Vm("return {on_event=function() " + code + " end}").Handle(new("update"), Data));

    [TestMethod]
    [DataRow("info")][DataRow("warning")][DataRow("error")]
    public void AllLevelsAndMaximumMessageAreAccepted(string level)
    {
        var vm = Vm("return {on_event=function() ui.log('" + level + "',string.rep('x',1024)) end}");
        vm.Handle(new("update"), Data); Assert.AreEqual(1024, vm.Log.Messages.Single().Message.Length);
        Assert.Throws<InterpreterException>(() => Vm("return {on_event=function() ui.log('info',string.rep('x',1025)) end}").Handle(new("update"), Data));
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public void CountAndTextOverflowAreCountedWithoutBreakingGameBatch(bool text)
    {
        var body = text ? "for i=1,10 do ui.log('info',string.rep('x',1024)) end" : "for i=1,40 do ui.log('info','x') end";
        var vm = Vm("return {on_event=function() " + body + "; ui.set_state('done') end}");
        Assert.AreEqual("done", vm.Handle(new("update"), Data).Single().State!.String);
        Assert.HasCount(text ? 8 : 32, vm.Log.Messages); Assert.AreEqual(text ? 2 : 8, vm.Log.Dropped);
    }

    [TestMethod]
    public void EmptyMessagesStillConsumeCountBudgetAndInvalidArgumentsAfterOverflowFail()
    {
        var vm = Vm("return {on_event=function() for i=1,33 do ui.log('info','') end; ui.log('bad','x') end}");
        Assert.Throws<InterpreterException>(() => vm.Handle(new("update"), Data));
        Assert.HasCount(32, vm.Log.Messages); Assert.AreEqual(1, vm.Log.Dropped);
    }

    [TestMethod]
    public void JournalEvictsOldEntriesAndFormatsControlsOnOnePhysicalLine()
    {
        var journal = new LuaDiagnosticJournal(); var session = Guid.NewGuid(); var owner = Guid.NewGuid(); var instance = Guid.NewGuid(); var scene = Guid.NewGuid();
        for (var i = 0; i < 300; i++) journal.Add(session, 3, owner, instance, scene, "click", new("info", "x\n\r\t\u001b"));
        Assert.HasCount(256, journal.Entries); Assert.AreEqual(44L, journal.Evicted); Assert.AreEqual(300L, journal.Sequence);
        var entry = journal.Entries[0]; Assert.AreEqual(45L, entry.Sequence); Assert.AreEqual(session, entry.SessionId); Assert.AreEqual(owner, entry.OwnerId);
        Assert.AreEqual(instance, entry.InstanceId); Assert.AreEqual(scene, entry.SceneId); Assert.AreEqual(3L, entry.Generation);
        Assert.IsFalse(entry.Format().Contains('\n')); StringAssert.Contains(entry.Format(), "line=unknown");
        var located = entry with { Data = new("warning", "x", scene, 9) }; StringAssert.Contains(located.Format(), "line=9");
        Assert.AreEqual(scene, JsonSerializer.Deserialize<LuaDiagnosticEntry>(JsonSerializer.Serialize(located))!.Data.ScriptId);
    }

    [TestMethod]
    [DataRow("bad")][DataRow("Scripts/bad.lua")][DataRow("Scripts/00000000000000000000000000000000.lua")][DataRow("Scripts/abc")]
    public void SourceNamesCannotBecomeArbitraryPaths(string name) => Assert.IsNull(LuaLogBuffer.ScriptId(name));

    [TestMethod]
    public void FaultClipsOnUnicodeBoundaryAndExtractsKnownSourceWithoutParsingUserPaths()
    {
        var id = Guid.NewGuid(); var fault = LuaLogBuffer.Fault(new InvalidOperationException($"Scripts/{id:N}.lua:(42,1-2): bad"));
        Assert.AreEqual(id, fault.ScriptId); Assert.AreEqual(42, fault.Line);
        var clipped = LuaLogBuffer.Clip(new string('x', 1023) + "😀tail"); Assert.AreEqual(1023, clipped.Length);
        Assert.AreEqual("short", LuaLogBuffer.Clip("short"));
        Assert.IsNull(LuaLogBuffer.Fault(new InvalidOperationException("unknown")).Line);
    }

    [TestMethod]
    [DataRow("level")][DataRow("null-text")][DataRow("long")][DataRow("empty-id")][DataRow("zero-line")][DataRow("big-line")]
    public void MessageValidationRejectsInvalidWireValues(string mutation)
    {
        var message = mutation switch
        {
            "level" => new LuaLogMessage("debug", "x"), "null-text" => new("info", null!), "long" => new("info", new('x', 1025)),
            "empty-id" => new("info", "x", Guid.Empty), "zero-line" => new("info", "x", Line: 0), _ => new("info", "x", Line: 32770),
        };
        Assert.Throws<ArgumentException>(() => message.Validate());
    }

    [TestMethod]
    [DataRow("default")][DataRow("null")][DataRow("count")][DataRow("chars")][DataRow("dropped")][DataRow("hello")][DataRow("close")][DataRow("fault")][DataRow("fault-level")]
    public void ProtocolRejectsMalformedJournalBeforeAnyCommit(string mutation)
    {
        var request = ScreenLuaProtocolTests.Request();
        if (mutation is "hello" or "close") request = request with { Kind = mutation == "hello" ? "handshake" : "close" };
        var response = ScreenLuaProtocolTests.Reply(request) with { Logs = [new("info", "x")] };
        response = mutation switch
        {
            "default" => response with { Logs = default }, "null" => response with { Logs = [null!] },
            "count" => response with { Logs = [.. Enumerable.Repeat(new LuaLogMessage("info", ""), 33)] },
            "chars" => response with { Logs = [.. Enumerable.Repeat(new LuaLogMessage("info", new('x', 1024)), 9)] },
            "dropped" => response with { DroppedLogs = -1 }, "fault" => response with { Fault = new("error", "bad") },
            "fault-level" => response with { Kind = "error", Error = "bad", Fault = new("info", "bad") }, _ => response,
        };
        Assert.Throws<IOException>(() => ScreenLuaConnection.Validate(request, response));
    }

    [TestMethod]
    public async Task RealWorkerLogsModulesAndFailedHandlerWithoutApplyingItsState()
    {
        var rootId = Guid.NewGuid(); var childId = Guid.NewGuid();
        const string source = "local m=ui.import(modules.helper)\nreturn {on_load=function() ui.set_state('before') end,on_event=function()\n m.call()\n ui.set_state('after')\n local x=nil; x()\nend}";
        var root = new LuaScriptAsset(rootId, "root", source) { References = Refs.Add("helper", childId) };
        var child = new LuaScriptAsset(childId, "helper", "ui.log('info','module load')\nreturn {call=function()\n ui.log('warning','module call')\nend}");
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, modules: new(rootId, [root, child])); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(childId, session.Journal.Entries.Single().Data.ScriptId); Assert.AreEqual(1, session.Journal.Entries.Single().Data.Line);
        session.Enqueue(instance, new("update"), Data); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("before", session.GetState(instance).String); Assert.IsFalse(session.IsAvailable(instance));
        Assert.HasCount(3, session.Journal.Entries); var call = session.Journal.Entries[1];
        Assert.AreEqual(childId, call.Data.ScriptId); Assert.AreEqual(3, call.Data.Line); Assert.AreEqual("update", call.Event);
        Assert.AreEqual(instance.Id, call.InstanceId); Assert.AreEqual(instance.OwnerId, call.OwnerId);
        var fault = session.Journal.Entries[2]; Assert.AreEqual("error", fault.Data.Level); Assert.AreEqual(rootId, fault.Data.ScriptId);
        Assert.AreEqual(5, fault.Data.Line, fault.Data.Message);
    }

    [TestMethod]
    public async Task RealWorkerBudgetsResetPerEventAndOverflowIsVisible()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() ui.log('info','load') end,on_event=function() for i=1,40 do ui.log('info','x') end end}");
        await ScreenLuaSessionTests.Drain(session);
        for (var i = 0; i < 2; i++) { session.Enqueue(instance, new("update"), Data); await ScreenLuaSessionTests.Drain(session); }
        Assert.HasCount(67, session.Journal.Entries); Assert.AreEqual(3, session.AppliedBatches);
        StringAssert.Contains(session.Journal.Entries.Last().Data.Message, "8");
        session.Reset(); Assert.HasCount(67, session.Journal.Entries); // history survives reset; context separates generations.
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task ClosedOrResetOwnersNeverPublishLateSuccessOrFailure(bool failure)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var call = await connection.Next(); stack.Close(instance);
        var response = ScreenLuaProtocolTests.Reply(call.Request) with { Logs = [new("info", "stale")] };
        if (failure) response = response with { Kind = "error", Error = "stale error", Fault = new("error", "stale error") };
        call.Completion.SetResult(response); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Journal.Entries); Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task ForgedScriptIdentityRejectsTheWholeResponse()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection(true);
        connection.Response = request => ScreenLuaProtocolTests.Reply(request) with { Logs = request.Kind == "load" ? [new("info", "forged", Guid.NewGuid(), 1)] : [] };
        await using var session = new ScreenLuaSession(stack, () => connection); session.Attach(instance, "return {}");
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual(0, session.AppliedBatches);
        Assert.IsFalse(session.Journal.Entries.Any(e => e.Data.Message == "forged")); StringAssert.Contains(session.Diagnostics.Single(), "чужой Lua-ресурс");
    }

    [TestMethod]
    public async Task SyntaxFaultAndTopLevelMessagesIdentifyTheActualBrokenModule()
    {
        var rootId = Guid.NewGuid(); var childId = Guid.NewGuid();
        const string source = "local m=ui.import(modules.helper); return {on_event=function() end}";
        var root = new LuaScriptAsset(rootId, "root", source) { References = Refs.Add("helper", childId) };
        var child = new LuaScriptAsset(childId, "child", "return {\n broken =\n}");
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, modules: new(rootId, [root, child])); await ScreenLuaSessionTests.Drain(session);
        var fault = session.Journal.Entries.Single(); Assert.AreEqual(childId, fault.Data.ScriptId); Assert.IsNotNull(fault.Data.Line);
        Assert.AreEqual(0, session.AppliedBatches);
    }

    [TestMethod]
    public void WireVersionTwelveIsExplicitlyRejectedAndJournalRoundTripsStrictly()
    {
        var request = ScreenLuaProtocolTests.Request(); var response = ScreenLuaProtocolTests.Reply(request) with { Logs = [new("info", "x", Line: 32769)] };
        var restored = JsonSerializer.Deserialize<ScreenLuaResponse>(JsonSerializer.Serialize(response, ScreenLuaProtocol.Json), ScreenLuaProtocol.Json)!;
        ScreenLuaConnection.Validate(request, restored); Assert.AreEqual(32769, restored.Logs.Single().Line);
        Assert.Throws<IOException>(() => ScreenLuaConnection.Validate(request, response with { Protocol = 12 }));
        var json = JsonSerializer.Serialize(response, ScreenLuaProtocol.Json).Replace("\"Level\":", "\"Unknown\":1,\"Level\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScreenLuaResponse>(json, ScreenLuaProtocol.Json));
        Assert.IsNotNull(new ScreenLuaWorkerService().Process(request with { Protocol = 12 }).Error);
    }
}
