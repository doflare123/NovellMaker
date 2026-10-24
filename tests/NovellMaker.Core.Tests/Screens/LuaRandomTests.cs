using System.Collections.Immutable;
using System.Text.Json;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class LuaRandomTests
{
    private static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty;
    private static readonly ImmutableDictionary<string, Guid> Refs = ImmutableDictionary<string, Guid>.Empty;
    private static ScreenLuaVm Vm(string source) => new(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(), source, Refs);
    private static ScreenLuaVm Body(string body) => Vm("return {on_load=function() " + body + " end,on_event=function() end}");

    [TestMethod]
    public void PcgMatchesPublishedReferenceSequenceAndRestoresExactly()
    {
        var snapshot = LuaRandomState.FromSeed(42); var value = snapshot.Value;
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        CollectionAssert.AreEqual(expected, expected.Select(_ => LuaRandomState.Step(ref value)).ToArray());
        var restored = LuaRandomState.Parse(LuaRandomState.Capture(value).ToString());
        var replay = restored.Value;
        Assert.AreEqual(LuaRandomState.Step(ref value), LuaRandomState.Step(ref replay));
        Assert.AreEqual(value, replay); Assert.AreNotEqual(snapshot.Value, value);
    }

    [TestMethod]
    [DataRow(0L)] [DataRow(1L)] [DataRow(42L)] [DataRow(4294967295L)]
    public void SnapshotsRoundTripWithoutLuaNumberPrecisionLoss(long seed)
    {
        var state = LuaRandomState.FromSeed((uint)seed);
        Assert.AreEqual(state, LuaRandomState.Parse(state.ToString()));
        Assert.AreEqual(state, JsonSerializer.Deserialize<LuaRandomState>(JsonSerializer.Serialize(state)));
        Assert.AreEqual(state, LuaRandomState.Parse(LuaValue.FromJson(LuaValue.FromString(state.ToString()).ToJson()).String!));
        Assert.AreEqual(25, state.ToString().Length);
    }

    [TestMethod]
    [DataRow("")] [DataRow("pcg32-v2:0000000000000000")] [DataRow("pcg32-v1:000000000000000")]
    [DataRow("pcg32-v1:00000000000000000")] [DataRow("pcg32-v1:FFFFFFFFFFFFFFFF")]
    [DataRow("pcg32-v1:000000000000000g")] [DataRow("pcg32-v1:0000000000000000\n")]
    public void InvalidSnapshotsAreRejected(string text)
    {
        Assert.Throws<ArgumentException>(() => LuaRandomState.Parse(text));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LuaRandomState>(JsonSerializer.Serialize(text)));
    }

    [TestMethod]
    [DataRow("123")] [DataRow("{}")] [DataRow("[]")] [DataRow("true")]
    public void SnapshotWireRequiresAString(string json) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LuaRandomState>(json));

    [TestMethod]
    [DataRow("pcg32-v1:0000000000000000")] [DataRow("pcg32-v1:ffffffffffffffff")]
    public void Every64BitStateHasALosslessCanonicalRepresentation(string text)
        => Assert.AreEqual(text, LuaRandomState.Parse(text).ToString());

    [TestMethod]
    [DataRow("ui.random(0)")] [DataRow("ui.random(-1)")] [DataRow("ui.random(2,1)")]
    [DataRow("ui.random(1.5)")] [DataRow("ui.random('2')")] [DataRow("ui.random(1/0)")]
    [DataRow("ui.random(0/0)")] [DataRow("ui.random(-2147483649,1)")]
    [DataRow("ui.random(1,2147483648)")] [DataRow("ui.random(1,2,3)")]
    [DataRow("ui.seed_random(-1)")] [DataRow("ui.seed_random(4294967296)")]
    [DataRow("ui.seed_random('1')")] [DataRow("ui.seed_random(1.5)")] [DataRow("ui.seed_random()")]
    [DataRow("ui.set_random_state(1)")] [DataRow("ui.set_random_state('bad')")]
    [DataRow("ui.get_random_state(1)")]
    public void InvalidLuaArgumentsFailBeforeSampling(string code)
        => Assert.Throws<InterpreterException>(() => Body(code).Handle(null, Empty));

    [TestMethod]
    [DataRow("ui.random()")] [DataRow("ui.seed_random(1)")]
    [DataRow("ui.get_random_state()")] [DataRow("ui.set_random_state('pcg32-v1:0000000000000000')")]
    public void TopLevelInitializationCannotUseHandlerRng(string code)
        => Assert.Throws<InterpreterException>(() => Vm(code + "; return {on_event=function() end}"));

    [TestMethod]
    public void LuaSeedReplayRangesAndSnapshotRestoreUseOneStream()
    {
        var vm = Body("""
            ui.seed_random(42)
            local first=ui.random()
            local saved=ui.get_random_state()
            local a=ui.random(-2147483648,2147483647)
            ui.set_random_state(saved)
            local b=ui.random(-2147483648,2147483647)
            local singleton=ui.random(-3,-3)
            ui.set_state({first=first,same=a==b,singleton=singleton,one=ui.random(1),die=ui.random(6)})
            """);
        var batch = vm.Handle(null, Empty);
        Assert.AreEqual(1, batch.Count(c => c.Kind == "random"));
        var data = batch.Single(c => c.Kind == "state").State!.Properties!;
        Assert.AreEqual(0xa15c02b7 / 4294967296d, data["first"].Number);
        Assert.IsTrue(data["same"].Boolean); Assert.AreEqual(-3, data["singleton"].Number);
        Assert.AreEqual(1, data["one"].Number); Assert.IsTrue(data["die"].Number is >= 1 and <= 6);
        Assert.IsNotNull(batch[0].RandomState);
    }

    [TestMethod]
    public void FloatCanBeZeroButNeverOneAndFullSignedRangeHasNoOverflow()
    {
        var batch = Body("ui.set_state({n=ui.random(),i=ui.random(-2147483648,2147483647)})")
            .Handle(null, Empty, randomState: LuaRandomState.Parse("pcg32-v1:0000000000000000"));
        var data = batch.Single(c => c.State is not null).State!.Properties!;
        Assert.AreEqual(0, data["n"].Number); Assert.IsTrue(data["i"].Number is >= int.MinValue and <= int.MaxValue);
        var samples = Body("local t={}; for i=1,500 do t[i]=ui.random() end; ui.set_state(t)").Handle(null, Empty);
        Assert.IsTrue(samples.Single(c => c.State is not null).State!.Items.All(v => v.Number is >= 0 and < 1));
    }

    [TestMethod]
    [DataRow("for i=1,1025 do ui.random() end")]
    [DataRow("for i=1,513 do ui.seed_random(1) end")]
    [DataRow("local s=ui.get_random_state(); for i=1,1025 do ui.set_random_state(s); ui.random() end")]
    public void InvocationQuotaCannotBeBypassedByReseedingOrRestoring(string body)
        => StringAssert.Contains(Assert.Throws<InterpreterException>(() => Body(body).Handle(null, Empty)).Message, "бюджет RNG");

    [TestMethod]
    public void ExactQuotaResetsPerEventAndSamplingRejectsBiasedLowWordsWithinTheSameQuota()
    {
        var vm = Vm("return {on_load=function() for i=1,1024 do ui.random() end end,on_event=function() for i=1,1024 do ui.random() end end}");
        Assert.HasCount(1, vm.Handle(null, Empty)); Assert.HasCount(1, vm.Handle(new("update"), Empty));
        // Zero emits zero, which must be rejected for a non-power-of-two width.
        var batch = Body("ui.set_state(ui.random(-2147483648,0))").Handle(null, Empty, randomState: LuaRandomState.Parse("pcg32-v1:0000000000000000"));
        var value = 0UL; var threshold = (uint)((1UL << 32) % 2147483649UL); uint word;
        do { word = LuaRandomState.Step(ref value); } while (word < threshold);
        Assert.AreEqual(-2147483648L + (long)(word % 2147483649UL), batch.Single(c => c.State is not null).State!.Number);
        Assert.AreEqual(value, batch[0].RandomState!.Value);
        Assert.Throws<ArgumentNullException>(() => LuaRandomState.Parse(null!));
    }

    [TestMethod]
    public void RemainingQuotaBoundsRejectionLoopsAndReseedAndDoesNotChangeDataOnLibraryFailure()
    {
        var script = new Script(CoreModules.None); var library = new LuaStandardLibrary(script); var api = new LuaRandomApi(library);
        var table = new Table(script); api.Install(table, () => true); script.Globals.Set("ui", DynValue.NewTable(table));
        api.Begin();
        script.DoString("for i=1,1023 do ui.random() end");
        Assert.Throws<InterpreterException>(() => script.DoString("ui.seed_random(0)"));
        script.DoString("ui.set_random_state('pcg32-v1:0000000000000000')");
        Assert.Throws<InterpreterException>(() => script.DoString("ui.random(-2147483648,0)"));
        library.ResetBudget(); api.Begin();
        for (var i = 0; i < LuaStandardLibrary.MaximumWork; i++) library.Charge(1);
        var state = api.State;
        Assert.Throws<InterpreterException>(() => script.DoString("ui.random()")); Assert.AreEqual(state, api.State);
    }

    [TestMethod]
    public void RngMetadataConsumesOneCommandAndCanPrecedeClose()
    {
        var valid = Body("ui.random(); ui.close()").Handle(null, Empty);
        Assert.AreEqual("random", valid[0].Kind); Assert.AreEqual("close", valid[1].Kind);
        Assert.Throws<InterpreterException>(() => Body("ui.random(); for i=1,256 do ui.set_state(i) end").Handle(null, Empty));
        Assert.IsEmpty(Body("ui.get_random_state()").Handle(null, Empty));
    }

    [TestMethod]
    public async Task HostCommitsOnlyOnPollAndKeepsRngOnErrorAndForgedBatch()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        var initial = LuaRandomState.FromSeed(42); var next = LuaRandomState.FromSeed(43);
        session.Attach(instance, "return {on_event=function() end}", randomState: initial); session.Poll(); var load = await connection.Next();
        Assert.AreEqual(initial, load.Request.RandomState);
        load.Reply(new ScreenChange("random", null, null, null) { RandomState = next });
        if (session.PendingTask is { } task) await task;
        Assert.AreSame(initial, session.GetRandomState(instance)); session.Poll(); Assert.AreSame(next, session.GetRandomState(instance));
        session.Enqueue(instance, new("update"), Empty); session.Poll(); var call = await connection.Next();
        Assert.AreEqual(next, call.Request.RandomState);
        call.Reply(new("random", null, null, null) { RandomState = initial }, new("random", null, null, null) { RandomState = next });
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(next, session.GetRandomState(instance)); Assert.IsNotEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task HostAdmissionFailureAfterValidWorkerBatchKeepsRandomStateAndView()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function() end}"); session.Poll(); var load = await connection.Next(); load.Reply();
        await ScreenLuaSessionTests.Drain(session);
        var before = instance.Definition; var random = session.GetRandomState(instance);
        session.Enqueue(instance, new("update"), Empty); session.Poll(); var call = await connection.Next();
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents; i++) session.Enqueue(instance, new("update"), Empty);
        call.Reply(new("random", null, null, null) { RandomState = LuaRandomState.FromSeed(42) }, new("text", before.Nodes[2].Id, "bad", null),
            new("trigger", null, null, null) { Trigger = new("x") });
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(random, session.GetRandomState(instance)); Assert.AreSame(before, instance.Definition);
    }

    [TestMethod]
    public async Task RealWorkerLuaSnapshotSurvivesTypedDataJsonAndExplicitRestore()
    {
        const string source = "return {on_load=function() local s=ui.get_state(); if s~=ui.null then ui.set_random_state(s.rng) end end,on_event=function() ui.set_state({value=ui.random(0,9999),rng=ui.get_random_state()}) end}";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, randomState: LuaRandomState.FromSeed(42)); await ScreenLuaSessionTests.Drain(session);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        var saved = LuaValue.FromJson(session.GetState(instance).ToJson());
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session); var expected = session.GetState(instance).ToJson();
        var restored = stack.Open(instance.OwnerId, instance.Definition); session.Attach(restored, source, state: saved);
        await ScreenLuaSessionTests.Drain(session); session.Enqueue(restored, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(expected, session.GetState(restored).ToJson()); Assert.AreEqual(session.GetRandomState(instance), session.GetRandomState(restored));
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LateReplyAfterCloseOrResetCannotPublishRng(bool reset)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection { IgnoreCancellation = true };
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function() end}"); session.Poll(); var call = await connection.Next();
        if (reset) session.Reset(); else stack.Close(instance);
        call.Reply(new ScreenChange("random", null, null, null) { RandomState = LuaRandomState.FromSeed(1) });
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual(0, session.AppliedBatches);
        Assert.Throws<ArgumentException>(() => session.GetRandomState(instance));
    }

    [TestMethod]
    public void UnknownClosedAndDisposedHandlesCannotReadRng()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection(true);
        var session = new ScreenLuaSession(stack, () => connection);
        Assert.Throws<ArgumentException>(() => session.GetRandomState(instance));
        session.Attach(instance, "return {}"); stack.Close(instance);
        Assert.Throws<ArgumentException>(() => session.GetRandomState(instance)); session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.GetRandomState(instance));
    }

    [TestMethod]
    [DataRow("missing")] [DataRow("duplicate")] [DataRow("kind")] [DataRow("node")] [DataRow("text")]
    [DataRow("value")] [DataRow("patch")] [DataRow("parent")] [DataRow("ids")]
    [DataRow("state")] [DataRow("service")] [DataRow("trigger")] [DataRow("after-close")]
    public void HostRejectsMalformedRngMetadata(string mutation)
    {
        var definition = ScreenLuaProtocolTests.Definition(); var change = new ScreenChange("random", null, null, null) { RandomState = LuaRandomState.Initial };
        var bad = mutation switch
        {
            "missing" => change with { RandomState = null }, "kind" => change with { Kind = "state" },
            "node" => change with { NodeId = definition.Nodes[0].Id }, "text" => change with { Text = "x" },
            "value" => change with { Value = true }, "patch" => change with { Patch = new() }, "parent" => change with { ParentId = definition.Nodes[0].Id },
            "ids" => change with { Ids = ImmutableDictionary<Guid, Guid>.Empty }, "state" => change with { State = LuaValue.Null },
            "service" => change with { Service = new(Guid.NewGuid(), GameServiceAction.MainMenu) },
            "trigger" => change with { Trigger = new("x") }, _ => change,
        };
        var changes = mutation == "duplicate" ? new[] { change, change } : mutation == "after-close" ? [new ScreenChange("close", null, null, null), change] : [bad];
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [.. changes]));
    }

    [TestMethod]
    public async Task WireRoundTripAndControlMessagesRejectExtraneousRng()
    {
        var request = ScreenLuaProtocolTests.Request() with { RandomState = LuaRandomState.Parse("pcg32-v1:ffffffffffffffff") };
        using var stream = new MemoryStream(); await ScreenLuaProtocol.WriteAsync(stream, request, CancellationToken.None); stream.Position = 0;
        Assert.AreEqual(request.RandomState, (await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(stream, CancellationToken.None)).RandomState);
        var worker = new ScreenLuaWorkerService(); var definition = ScreenLuaProtocolTests.Definition();
        var ready = ScreenLuaProtocolTests.Request("handshake");
        ready = ready with { Stamp = ready.Stamp with { OwnerId = Guid.Empty, InstanceId = Guid.Empty }, ResourceId = Guid.Empty, Event = null };
        Assert.IsNull(worker.Process(ready).Error);
        var load = ready with { Kind = "load", Stamp = ready.Stamp with { OwnerId = Guid.NewGuid(), InstanceId = Guid.NewGuid() },
            ResourceId = definition.Id, Nodes = definition.Nodes, Source = "return {on_event=function() end}" };
        Assert.IsNull(worker.Process(load).Error);
        Assert.IsNotNull(worker.Process(load with { Kind = "close", Source = null, Nodes = [], References = Refs, Stamp = load.Stamp with { Version = 1 }, RandomState = LuaRandomState.Initial }).Error);
        var hello = ScreenLuaProtocolTests.Request("handshake") with { RandomState = LuaRandomState.Initial };
        hello = hello with { Stamp = hello.Stamp with { OwnerId = Guid.Empty, InstanceId = Guid.Empty }, ResourceId = Guid.Empty, Event = null };
        Assert.IsNotNull(new ScreenLuaWorkerService().Process(hello).Error);
    }

    [TestMethod]
    [DataRow("ui.random(0)")] [DataRow("for i=1,1025 do ui.random() end")]
    [DataRow("ui.set_random_state('bad')")] [DataRow("while true do end")]
    public async Task RealWorkerFailureKeepsLastHostRngStateVisualsAndServices(string failure)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() ui.seed_random(42); ui.set_state(1) end,on_event=function() ui.random(); ui.set_state(2); ui.set_text(refs.label,'bad'); ui.save_slot('x'); " + failure + " end}", Refs.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        var random = session.GetRandomState(instance); var data = session.GetState(instance); var definition = instance.Definition;
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(random, session.GetRandomState(instance));
        Assert.AreSame(data, session.GetState(instance)); Assert.AreSame(definition, instance.Definition); Assert.IsEmpty(session.DrainServices());
    }

    [TestMethod]
    public async Task RealWorkerModuleAndHandlerShareStreamButInstancesAndNewAttachAreIndependent()
    {
        var root = Guid.NewGuid(); var child = Guid.NewGuid();
        const string source = "local m=ui.import(modules.child); return {on_load=function() end,on_event=function() local a=ui.random(); local b=m.draw(); ui.set_state({a,b}) end}";
        var modules = new LuaModuleLibrary(root, [new(root, "root", source) { References = Refs.Add("child", child) }, new(child, "child", "return {draw=function() return ui.random() end}")]);
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, modules: modules, randomState: LuaRandomState.FromSeed(42)); await ScreenLuaSessionTests.Drain(session);
        var saved = JsonSerializer.Deserialize<LuaRandomState>(JsonSerializer.Serialize(session.GetRandomState(instance)))!;
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        var expected = session.GetState(instance).ToJson(); var after = session.GetRandomState(instance);
        var restored = stack.Open(instance.OwnerId, instance.Definition); session.Attach(restored, source, modules: modules, randomState: saved);
        await ScreenLuaSessionTests.Drain(session); session.Enqueue(restored, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(expected, session.GetState(restored).ToJson()); Assert.AreEqual(after, session.GetRandomState(restored));
        var samples = session.GetState(restored).Items;
        Assert.AreEqual(0xa15c02b7 / 4294967296d, samples[0].Number); Assert.AreEqual(0x7b47f409 / 4294967296d, samples[1].Number);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(after, session.GetRandomState(restored)); Assert.AreNotEqual(after, session.GetRandomState(instance));
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task RealWorkerModulesCannotResetDrawQuotaOrCreateALoadTimeRandomStream()
    {
        var root = Guid.NewGuid(); var child = Guid.NewGuid();
        const string source = "local m=ui.import(modules.child); return {on_event=function() for i=1,700 do ui.random() end; m.draw() end}";
        var modules = new LuaModuleLibrary(root, [new(root, "root", source) { References = Refs.Add("child", child) },
            new(child, "child", "return {draw=function() for i=1,325 do ui.random() end end}")]);
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, modules: modules); await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(LuaRandomState.Initial, session.GetRandomState(instance)); StringAssert.Contains(session.Diagnostics[0], "бюджет RNG");
        var other = stack.Open(instance.OwnerId, instance.Definition);
        session.Attach(other, "ui.random(); return {on_event=function() end}"); await ScreenLuaSessionTests.Drain(session);
        StringAssert.Contains(session.Diagnostics[^1], "только в обработчике");
    }
}
