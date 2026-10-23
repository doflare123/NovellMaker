using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenLuaSessionTests
{
    internal sealed record Call(ScreenLuaRequest Request, TaskCompletionSource<ScreenLuaResponse> Completion)
    {
        internal void Reply(params ScreenChange[] changes) => Completion.SetResult(ScreenLuaProtocolTests.Reply(Request, changes));
    }
    internal sealed class Connection(bool automatic = false) : IScreenLuaConnection
    {
        private readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
        internal readonly ConcurrentQueue<ScreenLuaRequest> Seen = [];
        internal bool Disposed;
        internal bool IgnoreCancellation;
        internal bool FailDispose;
        internal Func<ScreenLuaRequest, ScreenLuaResponse>? Response;
        public async Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
        {
            Seen.Enqueue(request);
            if (request.Kind is "handshake" or "close" || automatic)
                return Response?.Invoke(request) ?? ScreenLuaProtocolTests.Reply(request);
            var call = new Call(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
            calls.Writer.TryWrite(call);
            return IgnoreCancellation ? await call.Completion.Task : await call.Completion.Task.WaitAsync(token);
        }
        internal async Task<Call> Next() => await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return FailDispose ? ValueTask.FromException(new IOException("cleanup failure")) : ValueTask.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class SchedulingClock(int expiresAfter = 3) : TimeProvider
    {
        private int reads;
        internal void ExpireAfterReads(int count) { reads = 0; expiresAfter = count; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Increment(ref reads) >= expiresAfter ? TimeSpan.FromSeconds(5).Ticks : 0;
    }
    internal static (ScreenStack Stack, ScreenInstance Instance) Fixture()
    {
        var stack = new ScreenStack(Guid.NewGuid());
        return (stack, stack.Open(Guid.NewGuid(), ScreenLuaProtocolTests.Definition()));
    }
    internal static async Task Drain(ScreenLuaSession session)
    {
        for (var i = 0; i < 1000 && session.HasPending; i++)
        {
            session.Poll();
            if (session.PendingTask is { } task) { try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { } }
            else await Task.Delay(1);
            session.Poll();
        }
        Assert.IsFalse(session.HasPending, "The screen queue did not settle.");
    }
    private static async Task Ready(ScreenLuaSession session)
    {
        if (session.PendingTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task DeadlineExpiringInBackgroundSchedulerBeforeAnyExchangeDoesNotPoisonConversation()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(true);
        await using var session = new ScreenLuaSession(stack, () => connection, new SchedulingClock());
        session.Attach(instance, "return {}"); session.Poll();
        try { await Ready(session); } catch (IOException) { }
        session.Poll();
        Assert.IsEmpty(connection.Seen); Assert.IsFalse(session.IsAvailable(instance));
        Assert.AreEqual(ScreenLuaIssueKind.QueueExpired, session.Issues.Single().Kind);
        Assert.IsFalse(connection.Disposed);
        var other = stack.Open(Guid.NewGuid(), instance.Definition); session.Attach(other, "return {}"); await Drain(session);
        Assert.IsTrue(session.IsAvailable(other)); Assert.AreEqual(1L, session.Generation); Assert.AreEqual(1L, other.Version);
    }

    [TestMethod]
    public async Task ExpiredControlCloseRetiresConversationRatherThanLeakingRemoteVm()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(true); var clock = new SchedulingClock(int.MaxValue);
        await using var session = new ScreenLuaSession(stack, () => connection, clock);
        session.Attach(instance, "return {}"); await Drain(session);
        stack.Close(instance); clock.ExpireAfterReads(3); session.Poll();
        try { await Ready(session); } catch (IOException) { }
        session.Poll(); await session.DisposeAsync();
        Assert.IsTrue(connection.Disposed); Assert.IsFalse(session.HasPending);
        Assert.AreEqual(ScreenLuaIssueKind.Conversation, session.Issues.Single().Kind);
        Assert.IsFalse(connection.Seen.Any(r => r.Kind == "close"));
    }

    [TestMethod]
    public async Task ClosingWhileUnsentEventExpiresStillDeliversControlCloseForLoadedVm()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(true); var clock = new SchedulingClock(int.MaxValue);
        await using var session = new ScreenLuaSession(stack, () => connection, clock);
        session.Attach(instance, "return {}"); await Drain(session);
        clock.ExpireAfterReads(3); session.EnqueueUsingInstanceSnapshot(instance, new("update")); session.Poll();
        stack.Close(instance);
        try { await Ready(session); } catch (IOException) { }
        session.Poll(); await Drain(session);
        Assert.IsTrue(session.Diagnostics.IsEmpty); Assert.IsFalse(connection.Disposed);
        Assert.AreEqual(1, connection.Seen.Count(r => r.Kind == "close"));
        Assert.IsFalse(connection.Seen.Any(r => r.Kind == "event"));
    }

    [TestMethod]
    public void NoOpBatchPreservesDefinitionIdentityButStillKeepsCloseCommand()
    {
        var definition = ScreenLuaProtocolTests.Definition(); var node = definition.Nodes[1];
        var batch = ScreenLuaProtocol.Prepare(definition, [new("text", node.Id, node.Text, null),
            new("enabled", node.Id, null, node.Enabled), new("visible", node.Id, null, node.Visible),
            new("close", null, null, null)]);
        Assert.AreSame(definition, batch.Definition); Assert.IsTrue(batch.Close);
        batch = ScreenLuaProtocol.Prepare(definition, [new("text", node.Id, "changed", null), new("text", node.Id, node.Text, null)]);
        Assert.AreSame(definition, batch.Definition); Assert.IsFalse(batch.Close);
    }

    [TestMethod]
    [DataRow("enabled", false)][DataRow("visible", false)][DataRow("parent-visible", false)]
    public async Task QueuedClickInvalidatedByEarlierHandlerDoesNotChangeVmVersionOrOtherScreens(string kind, bool value)
    {
        var (stack, instance) = Fixture(); var other = stack.Open(Guid.NewGuid(), instance.Definition);
        var connection = new Connection(true);
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Attach(other, "return {}"); await Drain(session);
        var button = instance.Definition.Nodes[1].Id;
        connection.Response = request => ScreenLuaProtocolTests.Reply(request,
            request.Stamp.InstanceId == instance.Id && request.Event?.Kind == "click"
                ? [new ScreenChange(kind == "parent-visible" ? "visible" : kind,
                    kind == "parent-visible" ? instance.Definition.Nodes[0].Id : button, null, value)] : []);
        session.EnqueueUsingInstanceSnapshot(instance, new("click", button));
        session.EnqueueUsingInstanceSnapshot(instance, new("click", button));
        session.EnqueueUsingInstanceSnapshot(other, new("update"));
        await Drain(session);
        Assert.AreEqual(1, connection.Seen.Count(r => r.Kind == "event" && r.Stamp.InstanceId == instance.Id));
        Assert.AreEqual(2L, instance.Version); Assert.AreEqual(2L, other.Version);
        Assert.IsTrue(session.IsAvailable(instance)); Assert.IsTrue(session.IsAvailable(other));
        Assert.AreEqual(ScreenLuaIssueKind.StaleEvent, session.Issues.Single().Kind);
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); await Drain(session);
        Assert.AreEqual(3L, instance.Version);
    }

    [TestMethod]
    public async Task ExpiredQueuedEventDoesNotDisableLoadedVmOrFreshWorkFromAnotherOwner()
    {
        var (stack, instance) = Fixture(); var other = stack.Open(Guid.NewGuid(), instance.Definition);
        var clock = new Clock(); var connection = new Connection(true);
        await using var session = new ScreenLuaSession(stack, () => connection, clock);
        session.Attach(instance, "return {}"); session.Attach(other, "return {}"); await Drain(session);
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); clock.Advance(5);
        session.EnqueueUsingInstanceSnapshot(other, new("update")); await Drain(session);
        Assert.AreEqual(1L, instance.Version); Assert.AreEqual(2L, other.Version);
        Assert.IsTrue(session.IsAvailable(instance)); Assert.IsTrue(session.IsAvailable(other));
        Assert.AreEqual(ScreenLuaIssueKind.QueueExpired, session.Issues.Single().Kind);
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); await Drain(session);
        Assert.AreEqual(2L, instance.Version); Assert.AreEqual(1L, session.Generation);
    }

    [TestMethod]
    public async Task EventSnapshotIsExplicitAndCapturedAtAdmissionWhileInstanceReuseIsNamed()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(true);
        await using var session = new ScreenLuaSession(stack, () => connection);
        var initial = ImmutableDictionary<string, string>.Empty.Add("value", "initial");
        session.Attach(instance, "return {}", snapshot: initial); await Drain(session);
        var fresh = initial.SetItem("value", "fresh");
        session.Enqueue(instance, new("update"), fresh);
        fresh = fresh.SetItem("value", "later");
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); await Drain(session);
        var events = connection.Seen.Where(r => r.Kind == "event").ToArray();
        Assert.AreEqual("fresh", events[0].Snapshot["value"]); Assert.AreEqual("initial", events[1].Snapshot["value"]);
        Assert.ThrowsExactly<ArgumentNullException>(() => session.Enqueue(instance, new("update"), null!));
    }

    [TestMethod]
    public async Task ConversationCrashInvalidatesAllVmsAndReopenNeverReplaysSurvivingScreens()
    {
        var (stack, first) = Fixture(); var other = stack.Open(Guid.NewGuid(), first.Definition);
        var broken = new Connection(); var fresh = new Connection(true);
        var connections = new Queue<Connection>([broken, fresh]);
        await using var session = new ScreenLuaSession(stack, () => connections.Dequeue());
        session.Attach(first, "return {}"); session.Poll(); var load = await broken.Next(); load.Reply(); await Ready(session); session.Poll();
        session.Attach(other, "return {}"); session.Poll(); load = await broken.Next(); load.Reply(); await Ready(session); session.Poll();
        session.EnqueueUsingInstanceSnapshot(first, new("update")); session.Poll(); var call = await broken.Next();
        session.EnqueueUsingInstanceSnapshot(other, new("update"));
        call.Completion.SetException(new IOException("process memory limit"));
        try { await Ready(session); } catch (IOException) { }
        session.Poll();
        Assert.IsFalse(session.IsAvailable(first)); Assert.IsFalse(session.IsAvailable(other));
        Assert.IsFalse(session.HasPending); Assert.AreEqual(1L, first.Version); Assert.AreEqual(1L, other.Version);
        Assert.AreEqual(ScreenLuaIssueKind.Conversation, session.Issues.Single().Kind);
        var reopened = stack.Open(first.OwnerId, first.Definition); session.Attach(reopened, "return {}"); await Drain(session);
        Assert.IsTrue(broken.Disposed); Assert.AreEqual(2L, session.Generation);
        Assert.IsTrue(session.IsAvailable(reopened)); Assert.IsFalse(session.IsAvailable(other));
        Assert.AreEqual(1, fresh.Seen.Count(r => r.Kind == "load"));
        Assert.IsFalse(fresh.Seen.Any(r => r.Kind == "event"));
    }

    [TestMethod]
    public async Task TextCommitPreservesPointerCaptureButDisablingCapturedButtonCancelsActivation()
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        stack.Route(new(ScreenInputKind.PointerDown, 20, 20));
        load.Reply(new ScreenChange("text", instance.Definition.Nodes[2].Id, "loaded", null)); await Ready(session); session.Poll();
        Assert.AreEqual(instance.Definition.Nodes[1].Id, stack.Route(new(ScreenInputKind.PointerUp, 20, 20)).Activation?.NodeId);
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); session.Poll(); var update = await connection.Next();
        stack.Route(new(ScreenInputKind.PointerDown, 20, 20));
        update.Reply(new ScreenChange("enabled", instance.Definition.Nodes[1].Id, null, false)); await Ready(session); session.Poll();
        Assert.IsNull(stack.Focus); Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 20, 20)).Activation);
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Commit(instance, 0, instance.Definition));
        Assert.AreEqual(2L, instance.Version);
        stack.Close(instance);
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Commit(instance, 2, instance.Definition));
    }

    [TestMethod]
    public async Task BackgroundDeadlineIsRecheckedMonotonicallyBeforeCommitEvenIfTimerHasNotFired()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(); var clock = new Clock();
        await using var session = new ScreenLuaSession(stack, () => connection, clock);
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        clock.Advance(5); load.Reply(new ScreenChange("text", instance.Definition.Nodes[2].Id, "late", null));
        try { await Ready(session); } catch (IOException) { }
        session.Poll(); Assert.AreEqual(0L, instance.Version); Assert.IsFalse(session.IsAvailable(instance));
        StringAssert.Contains(session.Diagnostics.Single(), "общий срок");
    }

    [TestMethod]
    public async Task ReopeningAfterProtocolFailureStartsFreshConversationAndKeepsOldHandlerDisabled()
    {
        var (stack, previous) = Fixture();
        var first = new Connection(true) { Response = request => ScreenLuaProtocolTests.Reply(request) with { Protocol = 42 } };
        var next = new Connection(true); var connections = new Queue<Connection>([first, next]);
        await using var session = new ScreenLuaSession(stack, () => connections.Dequeue());
        session.Attach(previous, "return {}"); session.Poll();
        try { await Ready(session); } catch (IOException) { }
        session.Poll(); Assert.IsFalse(session.IsAvailable(previous));
        var current = stack.Open(Guid.NewGuid(), previous.Definition); session.Attach(current, "return {}"); await Drain(session);
        Assert.AreEqual(2L, session.Generation); Assert.IsTrue(first.Disposed); Assert.IsTrue(session.IsAvailable(current));
        Assert.IsFalse(session.IsAvailable(previous)); Assert.IsTrue(session.Diagnostics.IsEmpty);
    }

    [TestMethod]
    public async Task FailedControlCloseInvalidatesWholeWorkerInsteadOfLeavingRemoteVmsAccumulating()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(true)
        {
            Response = request => request.Kind == "close" ? ScreenLuaProtocolTests.Reply(request) with { Kind = "error", Error = "close refused" }
                : ScreenLuaProtocolTests.Reply(request)
        };
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); await Drain(session);
        stack.Close(instance); session.Poll(); await Ready(session); session.Poll();
        Assert.IsFalse(session.HasPending); Assert.AreEqual("close refused", session.Diagnostics.Single());
        await session.DisposeAsync(); Assert.IsTrue(connection.Disposed);
    }

    [TestMethod]
    public async Task ClosingAnAlreadyLoadedScreenAfterTransportCrashDoesNotLeaveUndeliverableControlWork()
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        load.Reply(); await Ready(session); session.Poll();
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); session.Poll(); var update = await connection.Next();
        update.Completion.SetException(new IOException("worker crashed"));
        try { await Ready(session); } catch (IOException) { }
        session.Poll(); Assert.IsFalse(session.HasPending);
        stack.Route(new(ScreenInputKind.Back)); session.Poll();
        Assert.IsFalse(session.HasPending); Assert.IsTrue(stack.Instances.IsEmpty);
    }

    [TestMethod]
    public async Task CompletionNeverChangesLiveDefinitionBeforeHostPollAndCommitsOnce()
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        var before = instance.Definition;
        session.Attach(instance, "return {}", snapshot: ImmutableDictionary<string, string>.Empty.Add("volume", "1"));
        Assert.IsTrue(session.IsAvailable(instance)); Assert.IsTrue(session.HasPending);
        Assert.IsFalse(session.Poll()); var load = await connection.Next();
        Assert.IsFalse(session.Poll()); Assert.AreSame(before, instance.Definition); Assert.AreEqual(0L, instance.Version);
        load.Reply(new ScreenChange("text", before.Nodes[2].Id, "loaded", null)); await Ready(session);
        Assert.AreSame(before, instance.Definition);
        Assert.IsTrue(session.Poll()); Assert.AreEqual("loaded", instance.Definition.Nodes[2].Text);
        Assert.AreEqual(1L, instance.Version); Assert.AreEqual(1, session.AppliedBatches);
        Assert.IsFalse(session.Poll());
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("click", before.Nodes[1].Id)));
        session.Poll(); var click = await connection.Next();
        Assert.AreEqual(1L, click.Request.Stamp.Version); Assert.AreEqual(stack.SessionId, click.Request.Stamp.SessionId);
        Assert.IsTrue(click.Request.Nodes.IsEmpty); Assert.IsNull(click.Request.Source); Assert.IsTrue(click.Request.References.IsEmpty);
        Assert.AreEqual("1", click.Request.Snapshot["volume"]);
        click.Reply(new ScreenChange("close", null, null, null)); await Ready(session); session.Poll();
        Assert.IsTrue(stack.Instances.IsEmpty);
        await Drain(session);
        Assert.AreEqual("close", connection.Seen.Last().Kind);
        Assert.AreEqual(2L, connection.Seen.Last().Stamp.Version);
    }

    [TestMethod]
    [DataRow("close")][DataRow("replace")][DataRow("owner")][DataRow("clear")][DataRow("version")]
    public async Task ClosedReplacedClearedOrRevisedInstanceRejectsLateBatchAndClosesRemoteVm(string operation)
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        var before = instance.Definition;
        switch (operation)
        {
            case "close": stack.Close(instance); break;
            case "replace": stack.Replace(instance, before); break;
            case "owner": stack.CloseOwner(instance.OwnerId); break;
            case "clear": stack.Clear(); break;
            default: stack.Commit(instance, 0, before); break;
        }
        session.Poll();
        load.Reply(new ScreenChange("text", before.Nodes[2].Id, "stale", null)); await Ready(session); session.Poll();
        await Drain(session);
        Assert.AreSame(before, instance.Definition);
        Assert.IsFalse(session.IsAvailable(instance)); Assert.IsTrue(session.Diagnostics.IsEmpty);
        Assert.AreEqual(0, session.AppliedBatches);
        Assert.AreEqual(1, connection.Seen.Count(r => r.Kind == "close"));
    }

    [TestMethod]
    public async Task ResetCancelsOldConversationAndLateErrorCannotTouchNewGeneration()
    {
        var (stack, old) = Fixture(); var first = new Connection { IgnoreCancellation = true }; var second = new Connection(true);
        var connections = new Queue<Connection>([first, second]);
        await using var session = new ScreenLuaSession(stack, () => connections.Dequeue());
        session.Attach(old, "return {}"); session.Poll(); var late = await first.Next();
        stack.Clear(); session.Reset();
        var current = stack.Open(Guid.NewGuid(), ScreenLuaProtocolTests.Definition()); session.Attach(current, "return {}");
        late.Completion.SetResult(ScreenLuaProtocolTests.Reply(late.Request) with { Kind = "error", Error = "old error" });
        await Drain(session);
        Assert.AreEqual(2L, session.Generation); Assert.AreEqual(1L, current.Version);
        Assert.IsTrue(first.Disposed); Assert.IsTrue(session.Diagnostics.IsEmpty);
        Assert.AreEqual(1, session.AppliedBatches);
        Assert.IsTrue(second.Seen.All(r => r.Stamp.Generation == 2));
    }

    [TestMethod]
    public async Task MotionCoalescesOnlyAdjacentEventsAndSignificantQueueOverflowIsExplicit()
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("move", X: 1)));
        Assert.AreEqual(ScreenEventAdmission.Coalesced, session.EnqueueUsingInstanceSnapshot(instance, new("move", X: 2)));
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[1].Id)));
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("move", X: 3)));
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("hover", instance.Definition.Nodes[1].Id)));
        Assert.AreEqual(ScreenEventAdmission.Coalesced, session.EnqueueUsingInstanceSnapshot(instance, new("hover", instance.Definition.Nodes[1].Id, X: 9)));
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("hover", instance.Definition.Nodes[2].Id)));
        while (session.QueuedEvents < 128) Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[1].Id)));
        Assert.AreEqual(ScreenEventAdmission.Busy, session.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[1].Id)));
        Assert.AreEqual(128, session.QueuedEvents); StringAssert.Contains(session.Diagnostics.Last(), "не принято");
        load.Reply(); await Ready(session); session.Poll();
        var move = await connection.Next(); Assert.AreEqual(2d, move.Request.Event?.X);
        move.Reply(); await Ready(session); session.Poll();
        var click = await connection.Next(); Assert.AreEqual("click", click.Request.Event?.Kind);
        click.Reply(); await Ready(session); session.Poll();
        var laterMove = await connection.Next(); Assert.AreEqual(3d, laterMove.Request.Event?.X);
    }

    [TestMethod]
    public async Task FullEventQueueCannotPreventControlCloseAndOtherOwnersRemainAvailable()
    {
        var (stack, first) = Fixture(); var second = stack.Open(Guid.NewGuid(), first.Definition);
        var connection = new Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(first, "return {}"); session.Attach(second, "return {}"); await Drain(session);
        for (var i = 0; i < 128; i++) session.EnqueueUsingInstanceSnapshot(second, new("update"));
        stack.Close(first); session.Poll(); await Ready(session); session.Poll();
        Assert.IsTrue(connection.Seen.Any(r => r.Kind == "close" && r.Stamp.InstanceId == first.Id));
        Assert.IsTrue(session.IsAvailable(second)); await Drain(session);
        Assert.AreEqual(130, session.AppliedBatches);
    }

    [TestMethod]
    [DataRow("foreign-node")][DataRow("wrong-stamp")][DataRow("exception")][DataRow("cancelled")][DataRow("oversized")][DataRow("json")]
    public async Task CorruptTransportOrBatchFaultsClosedAndLeavesEngineBackAvailable(string failure)
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        var before = instance.Definition; session.Attach(instance, "return {}"); session.Poll(); var call = await connection.Next();
        switch (failure)
        {
            case "exception": call.Completion.SetException(new IOException("broken process")); break;
            case "json": call.Completion.SetException(new System.Text.Json.JsonException("invalid typed data")); break;
            case "cancelled": call.Completion.SetCanceled(); break;
            case "wrong-stamp": call.Completion.SetResult(ScreenLuaProtocolTests.Reply(call.Request) with { Stamp = call.Request.Stamp with { OwnerId = Guid.NewGuid() } }); break;
            case "oversized": call.Reply(new ScreenChange("text", before.Nodes[1].Id, new('x', 8193), null)); break;
            default: call.Reply(new("text", before.Nodes[1].Id, "partial", null), new("visible", Guid.NewGuid(), null, false)); break;
        }
        try { await Ready(session); } catch (Exception) { }
        Assert.IsFalse(session.Poll()); Assert.AreSame(before, instance.Definition);
        Assert.AreEqual(0L, instance.Version); Assert.IsFalse(session.IsAvailable(instance)); Assert.IsFalse(session.HasPending);
        Assert.IsFalse(session.Diagnostics.IsEmpty);
        Assert.IsTrue(stack.Route(new(ScreenInputKind.Back)).Consumed); Assert.IsTrue(stack.Instances.IsEmpty);
    }

    [TestMethod]
    public async Task LuaErrorDisablesOnlyItsHandlerAndPreservesViewAndOtherInstances()
    {
        var (stack, instance) = Fixture(); var other = stack.Open(Guid.NewGuid(), instance.Definition);
        var connection = new Connection(true) { Response = request => request.Kind == "load" && request.Stamp.InstanceId == instance.Id
            ? ScreenLuaProtocolTests.Reply(request) with { Kind = "error", Error = "handler failed" } : ScreenLuaProtocolTests.Reply(request) };
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Attach(other, "return {}"); await Drain(session);
        Assert.IsFalse(session.IsAvailable(instance)); Assert.IsTrue(session.IsAvailable(other)); Assert.AreEqual(0L, instance.Version);
        Assert.AreEqual(1L, other.Version); Assert.AreEqual("handler failed", session.Diagnostics.Single());
        Assert.AreEqual(ScreenEventAdmission.Unavailable, session.EnqueueUsingInstanceSnapshot(instance, new("update")));
    }

    [TestMethod]
    public async Task QueueDeadlineIncludesTimeBeforeDispatchAndDiagnosticsAreBounded()
    {
        var (stack, instance) = Fixture(); var clock = new Clock(); var connection = new Connection();
        await using var session = new ScreenLuaSession(stack, () => connection, clock);
        session.Attach(instance, "return {}"); clock.Advance(5);
        session.Poll(); Assert.IsFalse(session.HasPending); Assert.IsFalse(session.IsAvailable(instance));
        Assert.IsEmpty(connection.Seen); StringAssert.Contains(session.Diagnostics.Single(), "ожидания");
        var next = stack.Open(Guid.NewGuid(), instance.Definition);
        session.Attach(next, "return {}"); session.Poll();
        // Expiry before dispatch does not retire the shared conversation or change generation.
        for (var i = 0; i < 100 && session.PendingTask is null; i++) { await Task.Delay(1); session.Poll(); }
        var load = await connection.Next();
        for (var i = 0; i < 170; i++) session.EnqueueUsingInstanceSnapshot(next, new("update"));
        Assert.AreEqual(32, session.Diagnostics.Length);
        load.Reply(); await Ready(session); session.Poll();
        session.EnqueueUsingInstanceSnapshot(next, new("update"));
        Assert.ThrowsExactly<InvalidOperationException>(() => session.Attach(stack.Open(Guid.NewGuid(), instance.Definition), "return {}"));
    }

    [TestMethod]
    public async Task AttachRejectsForeignHandlesReferencesSourcesAndSnapshotsBeforeStartingProcess()
    {
        var (stack, instance) = Fixture(); var connection = new Connection(true);
        await using var session = new ScreenLuaSession(stack, () => connection);
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance with { }, "return {}"));
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance, new('a', 32769)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance, "return {}", ImmutableDictionary<string, Guid>.Empty.Add("other", Guid.NewGuid())));
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance, "return {}", ImmutableDictionary<string, Guid>.Empty.Add("bad-name", instance.Id)));
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance, "return {}", snapshot: ImmutableDictionary<string, string>.Empty.Add("x", new('a', 8193))));
        Assert.IsEmpty(connection.Seen);
        session.Attach(instance, "return {}");
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance, "return {}"));
        Assert.ThrowsExactly<ArgumentException>(() => session.EnqueueUsingInstanceSnapshot(instance, new("unknown")));
        Assert.ThrowsExactly<ArgumentException>(() => session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty.Add("", "x")));
        await Drain(session);
        Assert.ThrowsExactly<ArgumentException>(() => session.Attach(instance, "return {}"));
        session.Dispose(); Assert.IsFalse(session.Poll()); Assert.IsFalse(session.IsAvailable(instance));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Attach(instance, "return {}"));
        Assert.ThrowsExactly<ObjectDisposedException>(session.Reset);
    }

    [TestMethod]
    public async Task DisposeCancelsPendingWithoutWaitingAndLateResultCannotCommit()
    {
        var (stack, instance) = Fixture(); var connection = new Connection { IgnoreCancellation = true, FailDispose = true };
        var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var call = await connection.Next();
        session.Dispose(); session.Dispose();
        call.Reply(new ScreenChange("text", instance.Definition.Nodes[1].Id, "late", null));
        await session.DisposeAsync();
        Assert.AreEqual(0L, instance.Version); Assert.AreEqual("first", instance.Definition.Nodes[1].Text);
        Assert.IsTrue(connection.Disposed); Assert.IsFalse(session.HasPending);
        StringAssert.Contains(session.Diagnostics.Last(), "Очистка");
    }

    [TestMethod]
    public async Task RequestedShutdownCancellationDoesNotProduceFalseCleanupDiagnostic()
    {
        var (stack, instance) = Fixture(); var connection = new Connection();
        var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); await connection.Next();
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.IsTrue(connection.Disposed); Assert.IsTrue(session.Diagnostics.IsEmpty);
        Assert.AreEqual(0L, instance.Version); Assert.IsFalse(session.HasPending);
    }

    [TestMethod]
    public async Task RepeatedOpenCloseCleansRemoteInstancesAndNeverGrowsBeyondBoundedState()
    {
        var stack = new ScreenStack(Guid.NewGuid()); var connection = new Connection(true);
        await using var session = new ScreenLuaSession(stack, () => connection);
        var definition = ScreenLuaProtocolTests.Definition(); var owner = Guid.NewGuid();
        for (var i = 0; i < 150; i++)
        {
            var instance = stack.Open(owner, definition); session.Attach(instance, "return {}"); await Drain(session);
            stack.CloseOwner(owner); session.Poll(); await Drain(session);
            Assert.IsFalse(session.IsAvailable(instance)); Assert.AreEqual(0, session.QueuedEvents);
        }
        Assert.AreEqual(150, connection.Seen.Count(r => r.Kind == "close"));
        Assert.AreEqual(1, connection.Seen.Count(r => r.Kind == "handshake"));
        Assert.IsTrue(session.Diagnostics.IsEmpty); Assert.IsTrue(stack.Instances.IsEmpty);
    }
}
