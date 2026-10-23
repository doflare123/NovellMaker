using System.Collections.Immutable;
using System.Text.Json;
using MoonSharp.Interpreter;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ComponentStateTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;

    [TestMethod]
    [DataRow("null")] [DataRow("true")] [DataRow("false")] [DataRow("-12.5")]
    [DataRow("\"Привет 🌍\"")] [DataRow("[]")] [DataRow("{}")]
    [DataRow("{\"a\":[null,true,2,\"\",{},[]],\"b\":{\"c\":false}}")]
    public void TypedJsonRoundTripIsCanonicalAndPreservesEveryShape(string json)
    {
        var value = LuaValue.FromJson(json);
        using var document = JsonDocument.Parse(json);
        var canonical = JsonSerializer.Serialize(document.RootElement);
        Assert.AreEqual(canonical, value.ToJson());
        Assert.AreEqual(canonical, LuaValue.FromJson(JsonSerializer.Serialize(value)).ToJson());
        var script = new Script(CoreModules.None); var projection = new LuaValueProjection(script);
        Assert.AreEqual(canonical, projection.Read(projection.Write(value)).ToJson());
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(canonical), value.JsonByteCount);
    }

    [TestMethod]
    [DataRow("{\"a\":1,\"a\":2}")] [DataRow("1e999")] [DataRow("NaN")] [DataRow("Infinity")]
    [DataRow("{\"a\":1,\"\\u0061\":2}")] [DataRow("{\"a\":}")] [DataRow("[1")]
    [DataRow("/*comment*/null")] [DataRow("[1,]")] [DataRow("null true")]
    public void InvalidJsonNeverBecomesData(string json) => Assert.Throws<JsonException>(() => LuaValue.FromJson(json));

    [TestMethod]
    [DataRow(double.NaN)] [DataRow(double.PositiveInfinity)] [DataRow(double.NegativeInfinity)]
    public void OnlyFiniteNumbersAreData(double number) => Assert.Throws<ArgumentException>(() => LuaValue.FromNumber(number));

    [TestMethod]
    public void PublicFactoriesCopyInputsAndRejectMalformedData()
    {
        var items = new List<LuaValue> { LuaValue.Null }; var value = LuaValue.FromList(items); items.Clear();
        Assert.AreEqual("[null]", value.ToJson());
        var pairs = new Dictionary<string, LuaValue> { ["z"] = LuaValue.FromNumber(1), ["a"] = LuaValue.FromBoolean(true) };
        var map = LuaValue.FromObject(pairs); pairs.Clear(); Assert.AreEqual("{\"a\":true,\"z\":1}", map.ToJson());
        Assert.Throws<ArgumentException>(() => LuaValue.FromList([null!]));
        Assert.Throws<ArgumentException>(() => LuaValue.FromObject([new("x", LuaValue.Null), new("x", LuaValue.Null)]));
        Assert.Throws<ArgumentException>(() => LuaValue.FromObject([new(null!, LuaValue.Null)]));
        Assert.Throws<ArgumentNullException>(() => LuaValue.FromString(null!));
        Assert.Throws<ArgumentNullException>(() => LuaValue.FromList(null!));
        Assert.Throws<ArgumentNullException>(() => LuaValue.FromObject(null!));
        Assert.Throws<ArgumentNullException>(() => LuaValue.FromJson(null!));
        Assert.Throws<ArgumentException>(() => LuaValue.FromString("\ud800"));
        Assert.Throws<ArgumentException>(() => LuaValue.FromString("\udc00"));
        Assert.Throws<ArgumentException>(() => LuaValue.FromString("\ud800x"));
        Assert.Throws<ArgumentException>(() => LuaValue.FromObject([new("\ud800", LuaValue.Null)]));
    }

    [TestMethod]
    public void ExactDepthElementKeyStringAndAggregateBudgetsAreEnforced()
    {
        var value = LuaValue.Null;
        for (var i = 0; i < LuaValue.MaximumDepth; i++) value = LuaValue.FromList([value]);
        Assert.AreEqual(value.ToJson(), LuaValue.FromJson(value.ToJson()).ToJson());
        Assert.Throws<ArgumentException>(() => LuaValue.FromList([value]));
        Assert.Throws<JsonException>(() => LuaValue.FromJson("[" + value.ToJson() + "]"));
        var list = LuaValue.FromList(Enumerable.Repeat(LuaValue.Null, LuaValue.MaximumValues - 1));
        Assert.AreEqual(1023, LuaValue.FromJson(list.ToJson()).Items.Length);
        Assert.Throws<ArgumentException>(() => LuaValue.FromList(Enumerable.Repeat(LuaValue.Null, LuaValue.MaximumValues)));
        Assert.Throws<JsonException>(() => LuaValue.FromJson("[" + string.Join(',', Enumerable.Repeat("null", LuaValue.MaximumValues)) + "]"));
        var text = LuaValue.FromString(new('x', LuaValue.MaximumStringLength));
        Assert.AreEqual(text.String, LuaValue.FromJson(text.ToJson()).String);
        Assert.Throws<ArgumentException>(() => LuaValue.FromString(new('x', LuaValue.MaximumStringLength + 1)));
        Assert.Throws<JsonException>(() => LuaValue.FromJson("\"" + new string('x', LuaValue.MaximumStringLength + 1) + "\""));
        _ = LuaValue.FromObject([new(new('k', LuaValue.MaximumKeyLength), LuaValue.Null)]);
        Assert.Throws<ArgumentException>(() => LuaValue.FromObject([new(new('k', LuaValue.MaximumKeyLength + 1), LuaValue.Null)]));
        Assert.Throws<JsonException>(() => LuaValue.FromJson("{\"" + new string('k', LuaValue.MaximumKeyLength + 1) + "\":null}"));
        var exact = LuaValue.FromList(Enumerable.Repeat(text, 4));
        _ = LuaValue.FromJson(exact.ToJson());
        Assert.Throws<ArgumentException>(() => LuaValue.FromList([exact, LuaValue.FromString("x")]));
        Assert.Throws<JsonException>(() => LuaValue.FromJson("[" + exact.ToJson() + ",\"x\"]"));
        // JSON escaping, rather than character count, is the limiting budget here.
        var escaped = LuaValue.FromString(new('\u0001', 8192));
        Assert.Throws<ArgumentException>(() => LuaValue.FromList([escaped, escaped]));
        Assert.Throws<ArgumentException>(() => LuaValue.FromJson(new(' ', LuaValue.MaximumJsonBytes + 1)));
    }

    [TestMethod]
    [DataRow("nil", "null")] [DataRow("ui.null", "null")] [DataRow("false", "false")]
    [DataRow("{}", "{}")] [DataRow("ui.list({})", "[]")] [DataRow("ui.object({})", "{}")]
    [DataRow("{1,ui.null,false}", "[1,null,false]")]
    [DataRow("{name='value',nested=ui.list({})}", "{\"name\":\"value\",\"nested\":[]}")]
    public void VmProjectsTypedDataWithoutChangingStringVariables(string lua, string expected)
    {
        var vm = Vm("ui.set_state(" + lua + "); ui.set_text(refs.label,ui.get('story'))");
        var changes = vm.Handle(null, Empty.Add("story", "unchanged"));
        Assert.AreEqual(expected, changes.Single(c => c.Kind == "state").State!.ToJson());
        Assert.AreEqual("unchanged", changes.Single(c => c.Kind == "text").Text);
    }

    [TestMethod]
    [DataRow("ui.set_state(0/0)")] [DataRow("ui.set_state(1/0)")] [DataRow("ui.set_state(-1/0)")]
    [DataRow("local t={}; t.self=t; ui.set_state(t)")]
    [DataRow("ui.set_state({[1]='a',[3]='b'})")] [DataRow("ui.set_state({[1]='a',k='b'})")]
    [DataRow("ui.set_state({[true]=1})")] [DataRow("ui.set_state({[0]=1})")] [DataRow("ui.set_state({[1.5]=1})")]
    [DataRow("ui.set_state(function() end)")] [DataRow("ui.set_state(ui.set_text)")]
    [DataRow("ui.set_state(ui.object({1}))")] [DataRow("ui.set_state(ui.list({x=1}))")]
    [DataRow("ui.list(1)")] [DataRow("ui.object(ui.null)")]
    [DataRow("ui.null.x=1; ui.set_state(ui.null)")]
    [DataRow("local t={}; for i=1,17 do t={t} end; ui.set_state(t)")]
    [DataRow("local t={}; for i=1,1024 do t[i]=false end; ui.set_state(t)")]
    [DataRow("ui.set_state(string.rep('x',10000))")]
    [DataRow("local s='x'; for i=1,14 do s=s..s end; ui.set_state(s)")]
    [DataRow("local s='x'; for i=1,13 do s=s..s end; ui.set_state({s,s,s,s,s})")]
    [DataRow("local s='x'; for i=1,8 do s=s..s end; ui.set_state({[s]=1})")]
    [DataRow("ui.set_state(os.time())")] [DataRow("ui.set_state(io.open('file'))")]
    [DataRow("ui.set_state(luanet.import_type('System.IO.File'))")]
    public void UnsupportedLuaDataOrAmbientCapabilitiesAreRejected(string code)
    {
        var vm = Vm(code);
        Assert.Throws<Exception>(() => vm.Handle(null, Empty));
    }

    [TestMethod]
    public void ProjectionRejectsMetatablesAndAllowsRepeatedAcyclicReferences()
    {
        var script = new Script(CoreModules.None); var projection = new LuaValueProjection(script); var table = new Table(script);
        table.MetaTable = new Table(script);
        Assert.Throws<ScriptRuntimeException>(() => projection.Read(DynValue.NewTable(table)));
        table.MetaTable = null; table.Set("x", DynValue.NewNumber(2));
        var root = new Table(script); root.Set("a", DynValue.NewTable(table)); root.Set("b", DynValue.NewTable(table));
        Assert.AreEqual("{\"a\":{\"x\":2},\"b\":{\"x\":2}}", projection.Read(DynValue.NewTable(root)).ToJson());
        var other = new LuaValueProjection(script);
        Assert.Throws<ScriptRuntimeException>(() => projection.Read(other.Null));
    }

    [TestMethod]
    public void ProjectionCountsEveryAliasAndDictionaryKeyAndBoundsSerializedBytes()
    {
        var script = new Script(CoreModules.None); var projection = new LuaValueProjection(script);
        var text = DynValue.NewString(new string('x', 8192)); var list = new Table(script);
        for (var i = 1; i <= 4; i++) list.Set(i, text);
        Assert.AreEqual(4, projection.Read(DynValue.NewTable(list)).Items.Length);
        list.Set(5, DynValue.NewString("x"));
        Assert.Throws<ScriptRuntimeException>(() => projection.Read(DynValue.NewTable(list)));
        var map = new Table(script);
        for (var i = 0; i < 4; i++) map.Set(new string((char)('a' + i), 128), DynValue.NewString(new string('x', 8192 - 128)));
        Assert.AreEqual(4, projection.Read(DynValue.NewTable(map)).Properties!.Count);
        map.Set("overflow", DynValue.NewString("x"));
        Assert.Throws<ScriptRuntimeException>(() => projection.Read(DynValue.NewTable(map)));
        var vm = Vm("local s='\\001'; for i=1,13 do s=s..s end; ui.set_state(s); ui.set_state(s)");
        Assert.Throws<ScriptRuntimeException>(() => vm.Handle(null, Empty));
    }

    [TestMethod]
    public async Task HostStateDoesNotPublishBeforePollAndAHostBudgetFailurePreservesIt()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function() end}"); session.Poll(); var load = await connection.Next();
        load.Reply(new ScreenChange("state", null, null, null) { State = LuaValue.FromNumber(1) });
        if (session.PendingTask is { } task) await task;
        Assert.AreSame(LuaValue.Null, session.GetState(instance));
        session.Poll(); Assert.AreEqual(1d, session.GetState(instance).Number);
        session.Enqueue(instance, new("update"), Empty); session.Poll(); var call = await connection.Next();
        Assert.AreEqual(1d, call.Request.State!.Number);
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents; i++) session.Enqueue(instance, new("update"), Empty);
        call.Reply(new("state", null, null, null) { State = LuaValue.FromNumber(2) },
            new("text", before.Nodes[2].Id, "bad", null), new("trigger", null, null, null) { Trigger = new("event") });
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(1d, session.GetState(instance).Number); Assert.AreSame(before, instance.Definition);
        Assert.AreEqual(1, session.AppliedBatches); Assert.IsNotEmpty(session.Diagnostics);
    }

    [TestMethod]
    public void GetterAndSetterDoNotShareMutableTablesWithTheState()
    {
        var vm = Vm("""
            local s={count=1,values={ui.null,false}}
            ui.set_state(s); s.count=42
            local a=ui.get_state(); a.count=99
            local b=ui.get_state()
            ui.set_text(refs.label,b.count==1 and b.values[1]==ui.null and b.values[2]==false and 'ok' or 'bad')
            """);
        var changes = vm.Handle(null, Empty);
        Assert.AreEqual("ok", changes.Last().Text);
        Assert.AreEqual("{\"count\":1,\"values\":[null,false]}", changes[0].State!.ToJson());
        Assert.Throws<InterpreterException>(() => new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "ui.get_state(); return {on_event=function() end}", ImmutableDictionary<string, Guid>.Empty));
        Assert.Throws<InterpreterException>(() => new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "ui.set_state(1); return {on_event=function() end}", ImmutableDictionary<string, Guid>.Empty));
    }

    [TestMethod]
    public async Task RealWorkerStateCommitsAcrossEventsAndCanRestoreIntoANewInstance()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        const string code = """
            local function show()
              local s=ui.get_state()
              if s==ui.null then s={count=0,items=ui.list({})} end
              s.count=s.count+1
              ui.set_state(s)
              ui.set_text(refs.label,s.count==1 and 'one' or s.count==2 and 'two' or 'three')
            end
            return {on_load=show,on_event=show}
            """;
        var refs = ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id);
        session.Attach(instance, code, refs);
        Assert.AreSame(LuaValue.Null, session.GetState(instance));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("{\"count\":1,\"items\":[]}", session.GetState(instance).ToJson());
        session.Enqueue(instance, new("update"), Empty);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("two", instance.Definition.Nodes[2].Text);
        var data = LuaValue.FromJson(session.GetState(instance).ToJson());
        var other = stack.Open(instance.OwnerId, instance.Definition);
        session.Attach(other, code, refs, state: data);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("three", other.Definition.Nodes[2].Text);
        Assert.AreEqual("{\"count\":2,\"items\":[]}", session.GetState(instance).ToJson());
        Assert.IsEmpty(session.Diagnostics);
        stack.Close(other); Assert.Throws<ArgumentException>(() => session.GetState(other));
        session.Reset(); Assert.Throws<ArgumentException>(() => session.GetState(instance));
    }

    [TestMethod]
    public async Task RealWorkerFailureLeavesLastCommittedStateAndVisualsIntact()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var original = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            return {on_load=function() ui.set_state({count=1}) end,on_event=function()
              ui.set_state({count=99}); ui.set_text(refs.label,'bad'); ui.set_state(0/0)
            end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", original.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        var state = session.GetState(instance);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(state, session.GetState(instance)); Assert.AreSame(original, instance.Definition);
        Assert.IsNotEmpty(session.Diagnostics); Assert.IsFalse(session.IsAvailable(instance));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LateStateAndVisualsCannotCommitAfterCloseOrReset(bool reset)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function() end}"); session.Poll(); var call = await connection.Next();
        if (reset) session.Reset(); else stack.Close(instance);
        call.Reply(new("state", null, null, null) { State = LuaValue.FromNumber(9) }, new("text", before.Nodes[2].Id, "bad", null));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(0, session.AppliedBatches); Assert.AreSame(before, instance.Definition);
        Assert.Throws<ArgumentException>(() => session.GetState(instance));
    }

    [TestMethod]
    public async Task StatePayloadRoundTripsNullAndAbsenceSeparatelyOnTheWire()
    {
        using var buffer = new MemoryStream();
        var request = ScreenLuaProtocolTests.Request() with { State = LuaValue.Null };
        await ScreenLuaProtocol.WriteAsync(buffer, request, CancellationToken.None);
        await ScreenLuaProtocol.WriteAsync(buffer, request with { State = null }, CancellationToken.None);
        var response = ScreenLuaProtocolTests.Reply(request, new ScreenChange("state", null, null, null) { State = LuaValue.Null });
        await ScreenLuaProtocol.WriteAsync(buffer, response, CancellationToken.None);
        buffer.Position = 0;
        Assert.AreSame(LuaValue.Null, (await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(buffer, CancellationToken.None)).State);
        Assert.IsNull((await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(buffer, CancellationToken.None)).State);
        Assert.AreSame(LuaValue.Null, (await ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(buffer, CancellationToken.None)).Changes[0].State);
    }

    [TestMethod]
    public void HostRejectsWrongShapesAndAggregateBudgetBeforeAnyCommit()
    {
        var definition = ScreenLuaProtocolTests.Definition(); var state = new ScreenChange("state", null, null, null) { State = LuaValue.Null };
        foreach (var bad in new[] { state with { State = null }, state with { Kind = "close" }, state with { Text = "x" }, state with { Value = true },
            state with { NodeId = definition.Nodes[0].Id }, state with { ParentId = definition.Nodes[0].Id }, state with { Patch = new() },
            state with { Ids = ImmutableDictionary<Guid, Guid>.Empty }, state with { Trigger = new("a") },
            state with { Service = new(Guid.NewGuid(), NovellMaker.Core.Execution.GameServiceAction.MainMenu) } })
            Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [bad]));
        var large = state with { State = LuaValue.FromString(new('\u0001', 8192)) };
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [large, large]));
        Assert.AreSame(LuaValue.Null, ScreenLuaProtocol.Prepare(definition, [state]).State);
        Assert.AreEqual(2d, ScreenLuaProtocol.Prepare(definition, [state, state with { State = LuaValue.FromNumber(2) }]).State!.Number);
    }

    private static ScreenLuaVm Vm(string body)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        return new(definition, Guid.NewGuid(), "return {on_load=function() " + body + " end,on_event=function() end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id));
    }
}
