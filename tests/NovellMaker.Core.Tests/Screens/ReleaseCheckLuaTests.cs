using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ReleaseCheckLuaTests
{
    private static readonly ReleaseCheckConfiguration Config = new("author", "novel", "1.0.0");
    private static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty;
    private sealed class Provider : IReleaseCheckProvider
    {
        internal int Calls;
        internal readonly TaskCompletionSource<ReleaseProviderReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration configuration, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Reply.Task; } // Intentionally ignores cancellation.
        internal void Success() => Reply.SetResult(new(ReleaseCheckCode.Ok,
            new("1.0.0.0", "1.1.0.0", true, "https://github.com/author/novel/releases/tag/v1.1.0", DateTimeOffset.UtcNow)));
    }
    private sealed class Connection(bool real) : IScreenLuaConnection
    {
        private readonly ScreenLuaWorkerService worker = new();
        private readonly ScreenLuaConnection? remote = real ? new() : null;
        internal readonly List<ScreenLuaRequest> Seen = [];
        public async Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
        {
            using var frame = new MemoryStream();
            await ScreenLuaProtocol.WriteAsync(frame, request, token); frame.Position = 0;
            var value = await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(frame, token); Seen.Add(value);
            return remote is null ? worker.Process(value) : await remote.ExchangeAsync(value, token);
        }
        public ValueTask DisposeAsync() => remote?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
    private static ImmutableDictionary<string, Guid> Refs(ScreenInstance instance) => ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id);
    private static async Task Until(ScreenLuaSession session, Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!done())
        {
            if (timeout.IsCancellationRequested) Assert.Fail(string.Join("\n", session.Diagnostics));
            session.Poll(); await Task.Delay(1);
        }
    }
    private const string Source = """
        local id
        local function assert(value) if not value then ui.set_text('invalid','assertion failed') end end
        return {on_load=function() id=ui.check_game_release(); ui.set_text(refs.label,id) end,
          on_event=function(e)
            if e.kind=='service_result' then
              assert(e.request_id==id and e.operation=='CheckGameRelease' and e.provider=='github_releases_v1')
              assert(e.persisted==nil)
              if e.success then
                assert(e.release.current_version=='1.0.0.0' and e.release.latest_version=='1.1.0.0')
                assert(e.release.update_available and e.release.page_url=='https://github.com/author/novel/releases/tag/v1.1.0')
                assert(e.release.checked_at~=nil and not e.release.from_cache)
              else assert(e.release==nil) end
              ui.set_text(refs.label,e.code)
            elseif e.kind=='update' then ui.cancel_release_check(id) end
          end}
        """;

    [TestMethod]
    public async Task ProviderFailureAndProjectConfigurationStayOutsideLuaWireAndJournal()
    {
        const string secret = "PRIVATE-CREDENTIAL-DO-NOT-LOG";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        var configuration = new ReleaseCheckConfiguration("public-author-unique", "public-repository-unique", "1.0.0");
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, configuration);
        var connection = new Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service);
        session.Attach(instance, "return {on_load=function() ui.check_game_release() end,on_event=function(e) ui.set_text(refs.label,e.code) end}", Refs(instance));
        await Until(session, () => provider.Calls == 1);
        provider.Reply.SetException(new IOException(secret));
        await Until(session, () => instance.Definition.Nodes[2].Text == "InvalidResponse");
        var wire = System.Text.Json.JsonSerializer.Serialize(connection.Seen, ScreenLuaProtocol.Json);
        Assert.IsFalse(wire.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains(configuration.Owner, StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains(configuration.Repository, StringComparison.Ordinal));
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(session.Journal).Contains(secret, StringComparison.Ordinal));
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task RealRequestIdAndValidatedResultCrossWire(bool real)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var connection = new Connection(real); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service, () => Empty.Add("current", "snapshot")); session.Attach(instance, Source, Refs(instance));
        await Until(session, () => provider.Calls == 1);
        session.ConnectReleaseChecks(service);
        using var otherService = new ReleaseCheckService(Guid.NewGuid(), true, provider);
        Assert.Throws<InvalidOperationException>(() => session.ConnectReleaseChecks(otherService));
        Assert.IsTrue(session.HasReleaseChecks(instance)); Assert.AreEqual(1, session.ReservedServiceReplies);
        Assert.IsEmpty(session.DrainServices());
        provider.Success(); await Until(session, () => instance.Definition.Nodes[2].Text == "Ok");
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual(0, service.ReservationCount);
        Assert.IsFalse(session.HasReleaseChecks(instance));
        var result = connection.Seen.Single(r => r.Event?.Result is not null).Event!.Result!;
        Assert.AreEqual(GameServiceAction.CheckGameRelease, result.Action); result.Validate();
        Assert.AreEqual("snapshot", connection.Seen.Single(r => r.Event?.Result is not null).Snapshot["current"]);
    }

    [TestMethod]
    public async Task DisabledStillDeliversOneCorrelatedResultWithoutNetwork()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), false, provider);
        var connection = new Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service); session.Attach(instance, Source, Refs(instance));
        await Until(session, () => instance.Definition.Nodes[2].Text == "Disabled");
        Assert.AreEqual(0, provider.Calls); Assert.IsEmpty(session.Diagnostics);
        Assert.HasCount(1, connection.Seen.Where(r => r.Event?.Result is not null));
    }

    [TestMethod]
    [DataRow("ui.check_game_release(1)")] [DataRow("ui.check_game_release(nil)")]
    [DataRow("ui.cancel_release_check()")] [DataRow("ui.cancel_release_check(1)")]
    [DataRow("ui.cancel_release_check('bad')")] [DataRow("ui.cancel_release_check('00000000000000000000000000000000')")]
    [DataRow("ui.cancel_release_check('11111111111111111111111111111111',nil)")]
    public void StrictArityAndTypes(string expression)
    {
        var vm = new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "return {on_load=function() " + expression + " end,on_event=function() end}", ImmutableDictionary<string, Guid>.Empty);
        Assert.Throws<Exception>(() => vm.Handle(null, Empty));
    }

    [TestMethod]
    [DataRow("ui.check_game_release(); error('fail')")]
    [DataRow("ui.check_game_release(); ui.get_settings()")]
    [DataRow("ui.check_game_release(); ui.close()")]
    [DataRow("ui.check_game_release(); ui.trigger('test')")]
    [DataRow("ui.cancel_release_check('11111111111111111111111111111111')")]
    public async Task InvalidBatchHasNoVisualOrNetworkEffects(string body)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition; var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var connection = new Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service);
        session.Attach(instance, "return {on_load=function() ui.set_text(refs.label,'changed'); " + body + " end,on_event=function() end}", Refs(instance));
        await Until(session, () => !session.IsAvailable(instance));
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, session.ReservedServiceReplies);
    }

    [TestMethod]
    public async Task CancelProducesOneOriginalResultAndLateSuccessIsDiscarded()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var connection = new Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service); session.Attach(instance, Source, Refs(instance));
        await Until(session, () => provider.Calls == 1);
        session.Enqueue(instance, new("update"), Empty);
        await Until(session, () => instance.Definition.Nodes[2].Text == "Cancelled");
        provider.Success(); await Until(session, () => !service.HasRunningTransport);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("Cancelled", instance.Definition.Nodes[2].Text);
        Assert.HasCount(1, connection.Seen.Where(r => r.Event?.Result is not null));
        var owner = service.Attach(Guid.NewGuid(), session.Generation);
        service.Check(owner); service.Poll(); Assert.IsTrue(service.TryTakeResult(owner, out var result));
        Assert.AreEqual(ReleaseCheckCode.RateLimited, result!.Code); // Late success did not populate cache.
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task CloseOrResetDropsLateReplyAndCannotReachNewInstance(bool reset)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        await using var session = new ScreenLuaSession(stack, () => new Connection(false));
        session.ConnectReleaseChecks(service); session.Attach(instance, Source, Refs(instance));
        await Until(session, () => provider.Calls == 1);
        if (reset) { stack.Clear(); session.Reset(); } else { stack.Close(instance); session.Poll(); }
        Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, session.ReservedServiceReplies);
        var next = stack.Open(Guid.NewGuid(), ScreenLuaProtocolTests.Definition());
        session.Attach(next, "return {on_event=function(e) ui.set_text(refs.label,'unexpected') end}", Refs(next));
        await ScreenLuaSessionTests.Drain(session); var before = next.Definition;
        provider.Success(); await Until(session, () => !service.HasRunningTransport);
        await ScreenLuaSessionTests.Drain(session); Assert.AreSame(before, next.Definition);
    }

    [TestMethod]
    public async Task PausedResultRetainsReservationUntilHandlerCommitsAndForgeryIsRejected()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var connection = new Connection(false); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service); session.Attach(instance, Source, Refs(instance));
        await Until(session, () => provider.Calls == 1);
        var id = Guid.Parse(instance.Definition.Nodes[2].Text); var origin = Guid.NewGuid();
        var forged = new ReleaseCheckResult(id, ReleaseCheckCode.Disabled, null, null, origin, 1);
        Assert.AreEqual(ScreenEventAdmission.Unavailable, session.Enqueue(instance, new("service_result")
        { Result = new(id, GameServiceAction.CheckGameRelease, false, forged.Message, null, []) { ReleaseCheck = forged } }, Empty));
        session.Suspend(instance, true); provider.Success(); await Until(session, () => !service.HasRunningTransport);
        Assert.AreEqual(1, service.ReservationCount); Assert.AreEqual(1, session.ReservedServiceReplies);
        Assert.IsFalse(connection.Seen.Any(r => r.Event?.Result is not null));
        session.Suspend(instance, false); await Until(session, () => instance.Definition.Nodes[2].Text == "Ok");
        Assert.AreEqual(0, service.ReservationCount);
    }

    [TestMethod]
    public async Task ForeignCancellationRejectsWholePacketAndKeepsOriginalOwnerAlive()
    {
        var (stack, first) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        await using var session = new ScreenLuaSession(stack, () => new Connection(false));
        session.ConnectReleaseChecks(service); session.Attach(first, Source, Refs(first));
        await Until(session, () => provider.Calls == 1);
        var second = stack.Open(Guid.NewGuid(), ScreenLuaProtocolTests.Definition()); var before = second.Definition;
        session.Attach(second, "return {on_load=function() ui.set_text(refs.label,'bad'); ui.cancel_release_check('" + first.Definition.Nodes[2].Text + "') end,on_event=function() end}", Refs(second));
        await Until(session, () => !session.IsAvailable(second)); Assert.AreSame(before, second.Definition);
        Assert.IsTrue(session.HasReleaseChecks(first)); provider.Success();
        await Until(session, () => first.Definition.Nodes[2].Text == "Ok");
    }

    [TestMethod]
    [DataRow(31)] [DataRow(32)]
    public async Task CancellationDepthIsValidatedBeforeVisualCommit(int depth)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var connection = new Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service);
        session.Attach(instance, """
            local id; local n=0
            return {on_load=function() id=ui.check_game_release() end,on_event=function(e)
              if e.kind=='update' then ui.trigger('chain')
              elseif e.kind=='trigger' then n=n+1
            """ + $" if n<{depth} then ui.trigger('chain') else ui.set_text(refs.label,'cancel'); ui.cancel_release_check(id) end" + """
              elseif e.kind=='service_result' then ui.set_text(refs.label,e.code) end
            end}
            """, Refs(instance));
        await Until(session, () => provider.Calls == 1);
        var before = instance.Definition;
        session.Enqueue(instance, new("update"), Empty);
        if (depth == 31)
        {
            await Until(session, () => instance.Definition.Nodes[2].Text == "Cancelled");
            var result = connection.Seen.Single(r => r.Event?.Result is not null).Event!.Result!.ReleaseCheck!;
            Assert.AreEqual(32, result.Depth); Assert.AreEqual(0, session.ReservedServiceReplies);
            Assert.IsEmpty(session.Diagnostics);
        }
        else
        {
            await Until(session, () => !session.IsAvailable(instance));
            Assert.AreSame(before, instance.Definition);
            Assert.IsFalse(connection.Seen.Any(r => r.Event?.Result is not null));
            StringAssert.Contains(string.Join(" ", session.Diagnostics), "бюджет");
        }
        provider.Success(); await Until(session, () => !service.HasRunningTransport);
        Assert.AreEqual(0, service.ReservationCount);
    }

    [TestMethod]
    public async Task ResultDepth32IsAllowedButItsNextRequestIsRejectedAtomically()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider);
        var connection = new Connection(false); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service);
        session.Attach(instance, """
            local n=0
            return {on_load=function() ui.trigger('chain') end,on_event=function(e)
              if e.kind=='trigger' then n=n+1
                if n<31 then ui.trigger('chain') else ui.check_game_release() end
              elseif e.kind=='service_result' then ui.set_text(refs.label,'bad'); ui.check_game_release() end
            end}
            """, Refs(instance));
        var before = instance.Definition;
        await Until(session, () => !session.IsAvailable(instance));
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(0, provider.Calls);
        var result = connection.Seen.Single(r => r.Event?.Result is not null).Event!.Result!.ReleaseCheck!;
        Assert.AreEqual(32, result.Depth); Assert.AreEqual(0, service.ReservationCount);
    }

    [TestMethod]
    public async Task FullSharedQueueRejectsRequestBeforeNetworkAndViewEffects()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var connection = new ScreenLuaSessionTests.Connection(); await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service); session.Attach(instance, "return {on_event=function() end}");
        session.Poll(); var load = await connection.Next();
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents; i++)
            Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(instance, new("update"), Empty));
        var before = instance.Definition;
        load.Reply(new("text", instance.Definition.Nodes[2].Id, "bad", null),
            new("service", null, null, null) { Service = new(Guid.NewGuid(), GameServiceAction.CheckGameRelease) });
        await Until(session, () => !session.IsAvailable(instance));
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, session.ReservedServiceReplies);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(8)] [DataRow(17)]
    public void EarlierWireVersionsAreRejected(int version)
    {
        var worker = new ScreenLuaWorkerService();
        var response = worker.Process(ScreenLuaProtocolTests.Request("handshake") with { Protocol = version });
        Assert.AreEqual("error", response.Kind); Assert.IsEmpty(response.Changes);
    }

    [TestMethod]
    public void WireResultRejectsMismatchesAndUnsafeReleaseData()
    {
        var check = new ReleaseCheckResult(Guid.NewGuid(), ReleaseCheckCode.Ok,
            new("1.0.0.0", "1.1.0.0", true, "https://github.com/author/novel/releases/tag/v1.1.0", DateTimeOffset.UtcNow), null, Guid.NewGuid(), 1);
        var value = new GameServiceResult(check.RequestId, GameServiceAction.CheckGameRelease, true, check.Message, null, []) { ReleaseCheck = check };
        value.Validate();
        Assert.Throws<ArgumentException>(() => (value with { RequestId = Guid.NewGuid() }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { Action = GameServiceAction.GetSettings }).Validate());
        Assert.Throws<ArgumentException>(() => (check with { Depth = 33 }).Validate());
        Assert.Throws<ArgumentException>(() => (check with { RetryAfterSeconds = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (check with { Release = check.Release! with { PageUrl = "http://attacker.invalid/data" } }).Validate());
        Assert.Throws<ArgumentException>(() => (check with { Release = check.Release! with { UpdateAvailable = false } }).Validate());
    }

    [TestMethod]
    public async Task HostConfigurationChangeRevokesOwnerAndDiscardsItsLateReply()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        await using var session = new ScreenLuaSession(stack, () => new Connection(false));
        session.ConnectReleaseChecks(service); session.Attach(instance, Source, Refs(instance));
        await Until(session, () => provider.Calls == 1);
        var before = instance.Definition;
        service.Configure(null); session.Poll();
        Assert.IsFalse(session.IsAvailable(instance)); Assert.AreEqual(0, session.ReservedServiceReplies);
        provider.Success(); await Until(session, () => !service.HasRunningTransport);
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(0, service.ReservationCount);
    }
}
