using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Triggers;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ServiceReplyTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;
    private sealed class Worker : IScreenLuaConnection
    {
        private readonly ScreenLuaWorkerService worker = new();
        internal readonly List<ScreenLuaRequest> Seen = [];
        public Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
        { Seen.Add(request); return Task.FromResult(worker.Process(request)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ServiceResultsPreserveCausalityAcrossTriggersOrServiceOnlyLoops(bool triggers)
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var worker = new Worker();
        await using var scripts = new ScreenLuaSession(stack, () => worker);
        var source = triggers ? """
            return {on_load=function() ui.trigger('again') end,on_event=function(e)
              if e.kind=='trigger' then ui.get_persistent('count')
              elseif e.kind=='service_result' then ui.trigger('again') end
            end}
            """ : "return {on_load=function() ui.get_persistent('count') end,on_event=function(e) ui.get_persistent('count') end}";
        scripts.Attach(origin, source);
        var requests = 0;
        for (var i = 0; i < 40 && scripts.IsAvailable(origin); i++)
        {
            await ScreenLuaSessionTests.Drain(scripts);
            foreach (var invocation in scripts.DrainServices())
            {
                requests++;
                var command = invocation.Command;
                Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.Enqueue(origin, new("service_result")
                { Result = new(command.RequestId, command.Action, true, "", null, []) { Key = command.Key, ProfileSaved = true } }, Empty));
            }
        }
        await ScreenLuaSessionTests.Drain(scripts);
        Assert.IsFalse(scripts.IsAvailable(origin)); Assert.AreEqual(0, scripts.ReservedServiceReplies);
        Assert.IsTrue(scripts.Diagnostics.Any(s => s.Contains("глубины", StringComparison.Ordinal)));
        Assert.AreEqual(triggers ? 16 : 32, requests);
        Assert.AreEqual(AuthorTrigger.MaximumChainDepth, scripts.AppliedBatches);
    }

    [TestMethod]
    public async Task ReplyReplacesReservedCapacityAndRunsBeforeInputExactlyOnce()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var worker = new Worker();
        await using var scripts = new ScreenLuaSession(stack, () => worker);
        scripts.Attach(origin, "return {on_load=function() ui.get_persistent('flag') end,on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(scripts); var request = scripts.DrainServices().Single().Command;
        Assert.AreEqual(1, scripts.ReservedServiceReplies);
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents - 1; i++) Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.Enqueue(origin, new("update"), Empty));
        Assert.AreEqual(ScreenEventAdmission.Busy, scripts.Enqueue(origin, new("update"), Empty));
        Assert.AreEqual(ScreenEventAdmission.Busy, scripts.ReserveServiceReply(origin, new(Guid.NewGuid(), GameServiceAction.ListSlots)));
        var reply = new ScreenLuaEvent("service_result") { Result = new(request.RequestId, request.Action, true, "", null, []) { Key = request.Key, DataValue = "written", ProfileSaved = true } };
        Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.Enqueue(origin, reply, Empty));
        Assert.AreEqual(0, scripts.ReservedServiceReplies); Assert.AreEqual(ScreenLuaSession.MaximumQueuedEvents, scripts.QueuedEvents);
        Assert.AreEqual(ScreenEventAdmission.Unavailable, scripts.Enqueue(origin, reply, Empty));
        await ScreenLuaSessionTests.Drain(scripts);
        Assert.AreEqual("service_result", worker.Seen.First(r => r.Kind == "event").Event!.Kind);
        Assert.AreEqual(1, worker.Seen.Count(r => r.Event?.Result?.RequestId == request.RequestId));
        Assert.IsTrue(scripts.IsAvailable(origin));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ClosedOriginAndResetReleaseReservationsAndCannotFeedAReplacement(bool reset)
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture();
        await using var scripts = new ScreenLuaSession(stack, () => new Worker());
        scripts.Attach(origin, "return {on_load=function() ui.get_persistent('flag') end,on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(scripts); var command = scripts.DrainServices().Single().Command;
        if (reset) scripts.Reset(); else { stack.Close(origin); scripts.Poll(); }
        Assert.AreEqual(0, scripts.ReservedServiceReplies);
        var current = stack.Open(Guid.NewGuid(), origin.Definition); scripts.Attach(current, "return {on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(scripts);
        var reply = new ScreenLuaEvent("service_result") { Result = new(command.RequestId, command.Action, true, "", null, []) { Key = command.Key, ProfileSaved = true } };
        Assert.AreEqual(ScreenEventAdmission.Unavailable, scripts.Enqueue(origin, reply, Empty));
        Assert.AreEqual(ScreenEventAdmission.Unavailable, scripts.Enqueue(current, reply, Empty));
        Assert.IsTrue(scripts.IsAvailable(current));
    }

    [TestMethod]
    public async Task WrongIdentityAndActionCannotConsumeAReplyReservation()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var other = stack.Open(Guid.NewGuid(), origin.Definition);
        var worker = new Worker(); await using var scripts = new ScreenLuaSession(stack, () => worker);
        foreach (var instance in new[] { origin, other }) scripts.Attach(instance, "return {on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(scripts);
        var command = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.ListSlots);
        Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.ReserveServiceReply(origin, command));
        Assert.AreEqual(ScreenEventAdmission.Busy, scripts.ReserveServiceReply(origin, command));
        var reply = new ScreenLuaEvent("service_result") { Result = new(command.RequestId, command.Action, true, "", null, []) };
        Assert.AreEqual(ScreenEventAdmission.Unavailable, scripts.Enqueue(other, reply, Empty));
        Assert.AreEqual(ScreenEventAdmission.Unavailable, scripts.Enqueue(origin, reply with { Result = reply.Result! with { Action = GameServiceAction.MainMenu } }, Empty));
        Assert.AreEqual(1, scripts.ReservedServiceReplies);
        Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.Enqueue(origin, reply, Empty));
        await ScreenLuaSessionTests.Drain(scripts); Assert.AreEqual(0, scripts.ReservedServiceReplies);
    }

    [TestMethod]
    public async Task ExternalBindingReplyWaitsForInitialHandlerLoad()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var worker = new Worker();
        await using var scripts = new ScreenLuaSession(stack, () => worker);
        scripts.Attach(origin, "return {on_event=function(e) if e.kind=='service_result' then ui.set_text(refs.label,'answered') end end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", origin.Definition.Nodes[2].Id));
        var command = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.ListSlots);
        Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.ReserveServiceReply(origin, command));
        Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.Enqueue(origin, new("service_result")
        { Result = new(command.RequestId, command.Action, true, "", null, []) }, Empty));
        await ScreenLuaSessionTests.Drain(scripts);
        Assert.IsEmpty(scripts.Diagnostics); Assert.AreEqual("answered", origin.Definition.Nodes[2].Text);
        Assert.AreEqual("load", worker.Seen.First(r => r.Kind != "handshake").Kind);
    }

    [TestMethod]
    public async Task NoReplyCapacityRejectsAServiceBeforeVisualCommitOrHostExecution()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var before = origin.Definition;
        var connection = new ScreenLuaSessionTests.Connection(); await using var scripts = new ScreenLuaSession(stack, () => connection);
        scripts.Attach(origin, "return {on_event=function(e) end}"); scripts.Poll(); var call = await connection.Next();
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents; i++) scripts.Enqueue(origin, new("update"), Empty);
        call.Reply(new("text", before.Nodes[2].Id, "changed", null), new("service", null, null, null) { Service = new(Guid.NewGuid(), GameServiceAction.ListSlots) });
        await ScreenLuaSessionTests.Drain(scripts);
        Assert.AreSame(before, origin.Definition); Assert.IsEmpty(scripts.DrainServices()); Assert.AreEqual(0, scripts.ReservedServiceReplies);
        Assert.IsNotEmpty(scripts.Diagnostics);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    [TestMethod]
    public async Task ExpiredDeliveredReplyFailsItsOwnerExplicitlyInsteadOfLeavingItWaiting()
    {
        var (stack, origin) = ScreenLuaSessionTests.Fixture(); var clock = new Clock(); var worker = new Worker();
        await using var scripts = new ScreenLuaSession(stack, () => worker, clock);
        scripts.Attach(origin, "return {on_load=function() ui.get_persistent('flag') end,on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(scripts); var command = scripts.DrainServices().Single().Command;
        scripts.Enqueue(origin, new("service_result") { Result = new(command.RequestId, command.Action, true, "", null, []) { Key = command.Key, ProfileSaved = true } }, Empty);
        clock.Ticks = TimeSpan.FromSeconds(6).Ticks; await ScreenLuaSessionTests.Drain(scripts);
        Assert.IsFalse(scripts.IsAvailable(origin)); Assert.AreEqual(0, scripts.ReservedServiceReplies); Assert.IsNotEmpty(scripts.Diagnostics);
    }
}
