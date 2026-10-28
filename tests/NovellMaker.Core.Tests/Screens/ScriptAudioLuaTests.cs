using System.Collections.Immutable;
using System.Text.Json;
using MoonSharp.Interpreter;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Tests.Audio;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScriptAudioLuaTests
{
    private static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty;
    private static ScreenChange Command(ScriptAudioCommand command) => new("audio", null, null, null) { Audio = command };
    private static ImmutableDictionary<string, Guid> Refs(ScriptAudioFixture f) => ImmutableDictionary<string, Guid>.Empty.Add("sample", f.Asset);
    private static ScreenLuaVm Vm(ScriptAudioFixture f, string body) => new(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
        "return {on_load=function() " + body + " end,on_event=function() end}", ImmutableDictionary<string, Guid>.Empty, audioReferences: Refs(f));

    [TestMethod]
    public void LuaBuildsStrictCommandsAndProjectsTerminalEvent()
    {
        using var f = new ScriptAudioFixture(); var vm = Vm(f, "local id=ui.play_audio(audio.sample,'voice',true,0.2);ui.set_audio_volume(id,0.8);ui.stop_audio(id)");
        var changes = vm.Handle(null, Empty); Assert.HasCount(3, changes);
        Assert.AreEqual(AudioGroup.Voice, changes[0].Audio!.Group); Assert.AreEqual(.8, changes[1].Audio!.Volume);
        Assert.AreEqual(changes[0].Audio!.Id, changes[2].Audio!.Id);
        var roundtrip = JsonSerializer.Deserialize<ScreenChange[]>(JsonSerializer.Serialize(changes, ScreenLuaProtocol.Json), ScreenLuaProtocol.Json)!;
        CollectionAssert.AreEqual(changes.ToArray(), roundtrip);
        var d = ScreenLuaProtocolTests.Definition(); var id = Guid.NewGuid();
        vm = new(d, Guid.NewGuid(), "return {on_event=function(e) ui.set_text(refs.target,e.audio_id..':'..e.status..':'..e.message) end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", d.Nodes[1].Id));
        var e = new ScreenLuaEvent("audio_result") { Audio = new(id, "failed", "device") };
        ScreenLuaProtocol.ValidateEvent(d, e);
        Assert.AreEqual(id.ToString("N") + ":failed:device", vm.Handle(e, Empty).Single().Text);
    }

    [TestMethod]
    [DataRow("ui.play_audio(audio.sample,'effect',false,0)")]
    [DataRow("ui.play_audio(audio.sample,'sound',true,1)")]
    public void SupportedGroupsAndVolumeBoundariesAreAccepted(string body)
    { using var f = new ScriptAudioFixture(); Assert.HasCount(1, Vm(f, body).Handle(null, Empty)); }

    [TestMethod]
    [DataRow("ui.play_audio(audio.sample,'master',false,1)")]
    [DataRow("ui.play_audio(audio.sample,'effect',0,1)")]
    [DataRow("ui.play_audio(audio.sample,'effect',false,'1')")]
    [DataRow("ui.play_audio(audio.sample,'effect',false,0/0)")]
    [DataRow("ui.play_audio(audio.sample,'effect',false,2)")]
    [DataRow("ui.play_audio(audio.sample,'effect',false)")]
    [DataRow("ui.play_audio(audio.sample,'effect',false,1,1)")]
    [DataRow("ui.play_audio('00000000000000000000000000000001','effect',false,1)")]
    [DataRow("ui.play_audio('Assets/test.wav','effect',false,1)")]
    [DataRow("ui.play_audio(1,'effect',false,1)")]
    [DataRow("ui.stop_audio('bad')")]
    [DataRow("ui.stop_audio('00000000000000000000000000000000')")]
    [DataRow("ui.set_audio_volume('00000000000000000000000000000001',-1)")]
    public void LuaRejectsBadTypesUnknownAssetsAndArguments(string body)
    { using var f = new ScriptAudioFixture(); Assert.Throws<InterpreterException>(() => Vm(f, body).Handle(null, Empty)); }

    [TestMethod]
    public void TopLevelAudioCallsCannotCreateSources()
    {
        using var f = new ScriptAudioFixture();
        Assert.Throws<InterpreterException>(() => new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "ui.play_audio(audio.sample,'sound',true,1);return {on_event=function()end}", ImmutableDictionary<string, Guid>.Empty, audioReferences: Refs(f)));
    }

    [TestMethod]
    public void ProtocolRejectsExtraPayloadAndNonAudioEvents()
    {
        using var f = new ScriptAudioFixture(); var d = ScreenLuaProtocolTests.Definition(); var command = Command(f.Play());
        foreach (var c in new[] { command with { Audio = null }, command with { Text = "x" }, command with { Value = true }, command with { NodeId = Guid.NewGuid() },
            command with { AnimationId = Guid.NewGuid() }, command with { Kind = "close" }, command with { State = Scripting.LuaValue.Null } })
            Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(d, [c]));
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(d, [new("close", null, null, null), command]));
        var e = new ScreenLuaEvent("audio_result") { Audio = new(Guid.NewGuid(), "completed") };
        foreach (var v in new[] { e with { Audio = null }, e with { NodeId = Guid.NewGuid() }, e with { Kind = "update" }, e with { X = 1 }, e with { Seconds = .1 } })
            Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(d, v));
    }

    [TestMethod]
    public async Task ActualWorkerPlaysRaisesVolumeStopsAndDeliversExactlyOneResult()
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack); session.ConnectAudio(f.Service);
        session.Attach(instance, """
            local sound=nil
            local n=0
            return {on_load=function() sound=ui.play_audio(audio.sample,'effect',true,0.25) end,
              on_event=function(e)
                if e.kind=='click' then ui.set_audio_volume(sound,0.75);ui.stop_audio(sound)
                elseif e.kind=='audio_result' then n=n+1;ui.set_text(refs.target,e.status..':'..n) end
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("target", instance.Definition.Nodes[1].Id), audioReferences: Refs(f));
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual(1, session.ActiveAudio); Assert.IsTrue(session.HasAudio(instance));
        session.PollAudio(_ => true); Assert.IsFalse(f.Backend.Sources[0].Paused);
        session.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[1].Id)); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(.75f, f.Backend.Sources[0].Volume); Assert.AreEqual(0, f.Service.ActiveSources);
        session.PollAudio(_ => true); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("stopped:1", instance.Definition.Nodes[1].Text); Assert.IsFalse(session.HasAudio(instance));
        session.PollAudio(_ => true); Assert.IsFalse(session.HasPending); Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task CompletionOrOpenFailureConsumesReservedSpaceOnlyOnce(bool fail)
    {
        using var f = new ScriptAudioFixture(); if (fail) f.Backend.Failure = "open";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {on_load=function() ui.play_audio(audio.sample,'effect',false,1) end,on_event=function(e) ui.set_text(refs.target,e.status) end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", instance.Definition.Nodes[1].Id), audioReferences: Refs(f));
        await ScreenLuaSessionTests.Drain(session);
        if (!fail) f.Backend.Sources.Single().Finished = true;
        session.PollAudio(_ => true); Assert.IsTrue(session.HasAudio(instance)); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(fail ? "failed" : "completed", instance.Definition.Nodes[1].Text); Assert.IsFalse(session.HasAudio(instance));
        Assert.AreEqual(ScreenEventAdmission.Unavailable, session.EnqueueUsingInstanceSnapshot(instance, new("audio_result") { Audio = new(Guid.NewGuid(), "completed") }));
    }

    [TestMethod]
    [DataRow("close")] [DataRow("reset")] [DataRow("dispose")] [DataRow("failure")]
    public async Task LifecycleRemovesOnlyItsSourcesWithoutSendingResultsToDeadVm(string operation)
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {on_load=function() ui.play_audio(audio.sample,'voice',true,1) end,on_event=function() ui.stop_audio('foreign') end}", audioReferences: Refs(f));
        await ScreenLuaSessionTests.Drain(session); f.Service.Execute(f.Owner, f.Play());
        switch (operation)
        {
            case "close": stack.Close(instance); session.Poll(); break;
            case "reset": session.Reset(); break;
            case "dispose": session.Dispose(); break;
            default: session.EnqueueUsingInstanceSnapshot(instance, new("update")); await ScreenLuaSessionTests.Drain(session); break;
        }
        Assert.AreEqual(1, f.Service.ActiveSources); Assert.AreEqual(1, f.Backend.Sources[0].Disposals); Assert.AreEqual(0, session.ActiveAudio);
        Assert.IsFalse(session.HasAudio(instance));
    }

    [TestMethod]
    public async Task PendingSuspensionAndInactiveOwnerPauseSoundWhileAnotherOwnerCanContinue()
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => c); session.ConnectAudio(f.Service); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {}", audioReferences: Refs(f)); session.Poll(); var load = await c.Next(); load.Reply(Command(f.Play())); await session.PendingTask!; session.Poll();
        session.PollAudio(_ => true); var s = f.Backend.Sources[0]; Assert.IsFalse(s.Paused);
        session.Suspend(instance, true); session.PollAudio(_ => true); Assert.IsTrue(s.Paused);
        session.Suspend(instance, false); session.PollAudio(_ => false); Assert.IsTrue(s.Paused);
        session.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[1].Id)); session.Poll(); var update = await c.Next(); session.PollAudio(_ => true); Assert.IsTrue(s.Paused);
        var other = stack.Open(Guid.NewGuid(), instance.Definition); session.Attach(other, "return {}", audioReferences: Refs(f));
        update.Reply(); await session.PendingTask!; session.Poll(); var second = await c.Next(); second.Reply(Command(f.Play())); await session.PendingTask!; session.Poll();
        session.PollAudio(i => ReferenceEquals(i, other)); Assert.IsTrue(s.Paused); Assert.IsFalse(f.Backend.Sources[1].Paused);
        Assert.Throws<InvalidOperationException>(() => session.ConnectAudio(new AudioService(f.Project, f.Root, "ru", f.Backend)));
        stack.CloseOwner(instance.OwnerId); session.PollAudio(_ => true); Assert.AreEqual(1, f.Service.ActiveSources);
    }

    [TestMethod]
    [DataRow("foreign_asset")] [DataRow("foreign_source")] [DataRow("duplicate")] [DataRow("capacity")]
    public async Task HostRejectsWholeBatchBeforeAudioOrVisualSideEffects(string fault)
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => c); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {}", audioReferences: Refs(f)); session.Poll(); var load = await c.Next(); load.Reply(); await session.PendingTask!; session.Poll();
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); session.Poll(); var work = await c.Next(); var play = f.Play();
        if (fault == "capacity") for (var i = 0; i < 128; i++) session.EnqueueUsingInstanceSnapshot(instance, new("update"));
        var bad = fault switch { "foreign_asset" => Command(f.Play() with { AssetId = Guid.NewGuid() }), "foreign_source" => Command(new("stop", Guid.NewGuid())),
            "duplicate" => Command(play), _ => Command(f.Play()) };
        var before = instance.Definition;
        work.Reply(new("text", before.Nodes[1].Id, "must not commit", null), Command(play), bad);
        await session.PendingTask!; session.Poll(); await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(before, instance.Definition); Assert.IsEmpty(f.Backend.Sources); Assert.IsFalse(session.IsAvailable(instance));
    }

    [TestMethod]
    public async Task LateReplyAndOverfullQueueCannotLeakSourcesOrLoseTerminalResults()
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => c); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {}", audioReferences: Refs(f)); session.Poll(); var load = await c.Next(); stack.Close(instance);
        load.Reply(Command(f.Play())); await session.PendingTask!; session.Poll(); await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(f.Backend.Sources);
        instance = stack.Open(Guid.NewGuid(), instance.Definition); session.Attach(instance, "return {}", audioReferences: Refs(f));
        session.Poll(); load = await c.Next(); load.Reply(Command(f.Play())); await session.PendingTask!; session.Poll();
        for (var i = 0; i < 127; i++) Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("update")));
        Assert.AreEqual(ScreenEventAdmission.Busy, session.EnqueueUsingInstanceSnapshot(instance, new("update")));
        f.Service.Stop(); session.PollAudio(_ => true); Assert.AreEqual(128, session.QueuedEvents); Assert.IsTrue(session.HasAudio(instance));
        stack.Close(instance); session.Poll(); Assert.AreEqual(0, session.QueuedEvents); Assert.AreEqual(0, f.Service.ActiveSources);
    }

    [TestMethod]
    public async Task DoubleStopWhileResultIsQueuedRemainsIdempotentUntilItsHandlerCommits()
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => c); session.ConnectAudio(f.Service); var play = f.Play();
        session.Attach(instance, "return {}", audioReferences: Refs(f)); session.Poll(); var load = await c.Next(); load.Reply(Command(play)); await session.PendingTask!; session.Poll();
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); session.Poll(); var first = await c.Next();
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); first.Reply(Command(new("stop", play.Id))); await session.PendingTask!; session.Poll();
        var second = await c.Next(); session.PollAudio(_ => true); Assert.IsTrue(session.HasAudio(instance));
        second.Reply(Command(new("volume", play.Id, Volume: .8)), Command(new("stop", play.Id))); await session.PendingTask!; session.Poll();
        var result = await c.Next(); Assert.AreEqual("audio_result", result.Request.Event!.Kind); Assert.AreEqual("stopped", result.Request.Event.Audio!.Status);
        result.Reply(); await session.PendingTask!; session.Poll(); Assert.IsFalse(session.HasAudio(instance));
        Assert.HasCount(1, f.Results); Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task ChainOfFailedPlayResultsStopsAtBoundedDepthAndCleansReservations()
    {
        using var f = new ScriptAudioFixture(); f.Backend.Failure = "open"; var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {on_load=function() ui.play_audio(audio.sample,'effect',false,1) end,on_event=function() ui.play_audio(audio.sample,'effect',false,1) end}", audioReferences: Refs(f));
        await ScreenLuaSessionTests.Drain(session);
        for (var i = 0; i < 35 && session.IsAvailable(instance); i++) { session.PollAudio(_ => true); await ScreenLuaSessionTests.Drain(session); }
        Assert.IsFalse(session.IsAvailable(instance)); Assert.HasCount(32, f.Results); Assert.AreEqual(0, session.ActiveAudio);
        Assert.IsFalse(session.HasAudio(instance)); StringAssert.Contains(session.Diagnostics.Single(), "глубина");
    }

    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task ActualWorkerMixedAudioAnimationChainCannotResetItsDepth(bool cancel, bool trigger)
    {
        using var f = new ScriptAudioFixture(); f.Backend.Failure = "open"; var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack); session.ConnectAudio(f.Service);
        var begin = "local id=ui.animate(ui.tween(refs.target,{x=20},0.001));" + (cancel ? "ui.cancel_animation(id);" : "");
        var body = """
            local function play() ui.play_audio(audio.sample,'effect',false,1) end
            return {on_load=play,on_event=function(e)
            """ + (trigger
                ? "if e.kind=='audio_result' then ui.trigger('bridge') elseif e.kind=='trigger' then " + begin
                : "if e.kind=='audio_result' then " + begin) +
            " elseif e.kind=='animation_result' then play() end end}";
        session.Attach(instance, body, ImmutableDictionary<string, Guid>.Empty.Add("target", instance.Definition.Nodes[1].Id), audioReferences: Refs(f));
        await ScreenLuaSessionTests.Drain(session);
        for (var i = 0; i < 45 && session.IsAvailable(instance); i++)
        {
            session.PollAudio(_ => true); await ScreenLuaSessionTests.Drain(session);
            session.AdvanceAnimations(TimeSpan.FromSeconds(1), _ => true); await ScreenLuaSessionTests.Drain(session);
        }
        Assert.IsFalse(session.IsAvailable(instance)); Assert.HasCount(trigger ? 11 : 16, f.Results);
        StringAssert.Contains(session.Diagnostics.Single(), "глубина");
        Assert.AreEqual(0, session.ActiveAudio); Assert.AreEqual(0, session.ActiveAnimations); Assert.AreEqual(0, session.QueuedEvents);
        Assert.AreEqual(0, f.Service.ActiveSources);
    }

    [TestMethod]
    public async Task TransportFailureDisposesEveryOwnerAndRepeatedOpenCloseDoesNotAccumulateResources()
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection(true);
        c.Response = r => ScreenLuaProtocolTests.Reply(r, r.Kind == "load" ? [Command(f.Play())] : []);
        await using var session = new ScreenLuaSession(stack, () => c); session.ConnectAudio(f.Service);
        for (var i = 0; i < 50; i++)
        {
            session.Attach(instance, "return {}", audioReferences: Refs(f)); await ScreenLuaSessionTests.Drain(session);
            Assert.AreEqual(1, f.Service.ActiveSources); stack.Close(instance); session.PollAudio(_ => true); await ScreenLuaSessionTests.Drain(session);
            Assert.AreEqual(0, f.Service.ActiveSources); Assert.AreEqual(0, session.ActiveAudio); instance = stack.Open(Guid.NewGuid(), instance.Definition);
        }
        session.Attach(instance, "return {}", audioReferences: Refs(f)); await ScreenLuaSessionTests.Drain(session);
        var other = stack.Open(Guid.NewGuid(), instance.Definition); session.Attach(other, "return {}", audioReferences: Refs(f)); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(2, f.Service.ActiveSources); c.Response = _ => throw new IOException("broken pipe");
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(0, f.Service.ActiveSources); Assert.IsFalse(session.IsAvailable(other)); Assert.IsTrue(f.Backend.Sources.All(s => s.Disposals == 1));
        Assert.AreEqual(0, session.ActiveAudio);
    }

    [TestMethod]
    public async Task MissingServiceAndInvalidReferenceCatalogFailBeforeCommit()
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection(true);
        c.Response = r => ScreenLuaProtocolTests.Reply(r, r.Kind == "load" ? [Command(f.Play())] : []);
        await using var session = new ScreenLuaSession(stack, () => c);
        Assert.Throws<ArgumentException>(() => session.Attach(instance, "return {}", audioReferences: Refs(f).Add("bad name", f.Asset)));
        session.Attach(instance, "return {}", audioReferences: Refs(f)); await ScreenLuaSessionTests.Drain(session);
        Assert.IsFalse(session.IsAvailable(instance)); Assert.IsEmpty(f.Backend.Sources);
        session.ConnectAudio(f.Service); using var second = new AudioService(f.Project, f.Root, "ru", f.Backend); session.ConnectAudio(second);
        session.Dispose(); Assert.Throws<ObjectDisposedException>(() => session.ConnectAudio(f.Service)); Assert.Throws<ObjectDisposedException>(() => session.PollAudio(_ => true));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task QueuedPendingOrHeldUpdateStreamsAudioButInputAndSuspensionStillHoldIt(bool holdReadyUpdate)
    {
        using var f = new ScriptAudioFixture(); var (stack, instance) = ScreenLuaSessionTests.Fixture(); var c = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => c); session.ConnectAudio(f.Service);
        session.Attach(instance, "return {}", audioReferences: Refs(f)); session.Poll(); var load = await c.Next();
        load.Reply(Command(f.Play())); await session.PendingTask!; session.Poll(); session.PollAudio(_ => true);
        var source = f.Backend.Sources.Single(); Assert.IsFalse(source.Paused);
        session.EnqueueUsingInstanceSnapshot(instance, new("update", Seconds: .05)); session.PollAudio(_ => true); Assert.IsFalse(source.Paused);
        session.Poll(); var update = await c.Next(); session.PollAudio(_ => true); Assert.IsFalse(source.Paused);
        if (holdReadyUpdate)
        {
            session.Suspend(instance, true); update.Reply(); await session.PendingTask!; session.Poll();
            session.PollAudio(_ => true); Assert.IsTrue(source.Paused);
            session.Suspend(instance, false); session.PollAudio(_ => true); Assert.IsFalse(source.Paused);
            session.Poll();
        }
        session.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[1].Id));
        session.PollAudio(_ => true); Assert.IsTrue(source.Paused);
        if (!holdReadyUpdate) { update.Reply(); await session.PendingTask!; session.Poll(); }
        else session.Poll();
        var click = await c.Next(); session.PollAudio(_ => true); Assert.IsTrue(source.Paused);
        click.Reply(); await session.PendingTask!; session.Poll(); session.PollAudio(_ => true); Assert.IsFalse(source.Paused);
        session.PollAudio(_ => false); Assert.IsTrue(source.Paused); session.PollAudio(_ => true); Assert.IsFalse(source.Paused);
        Assert.HasCount(1, f.Backend.Sources); Assert.AreEqual(0, source.Disposals); Assert.IsEmpty(session.Diagnostics);
    }
}
