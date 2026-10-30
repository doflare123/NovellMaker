using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ReleaseFailureProtocolTests
{
    private sealed class Provider : IReleaseCheckProvider
    {
        internal int Calls;
        internal readonly TaskCompletionSource<ReleaseProviderReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration config, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Reply.Task; }
    }

    /// <summary>Corrupt only the returning frame, after a genuine isolated worker computed it.</summary>
    private sealed class Connection(string failure) : IScreenLuaConnection
    {
        private readonly ScreenLuaConnection remote = new();
        private ScreenLuaResponse? previous;
        internal int Loads, Results;
        public async Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
        {
            var response = await remote.ExchangeAsync(request, token);
            if (request.Kind == "load") { Loads++; previous = response; }
            if (request.Event?.Result?.Action != GameServiceAction.CheckGameRelease) return response;
            Results++;
            if (failure == "duplicate") return previous!; // Replays the already committed request batch.
            if (failure == "lost") throw new IOException("Response lost after worker execution");
            var stamp = response.Stamp;
            return response with { Stamp = failure switch
            {
                "request" => stamp with { RequestId = Guid.NewGuid() },
                "session" => stamp with { SessionId = Guid.NewGuid() },
                "generation" => stamp with { Generation = stamp.Generation - 1 },
                "owner" => stamp with { OwnerId = Guid.NewGuid() },
                "instance" => stamp with { InstanceId = Guid.NewGuid() },
                _ => stamp with { Version = stamp.Version - 1 }
            } };
        }
        public ValueTask DisposeAsync() => remote.DisposeAsync();
    }

    private sealed class HeldCallbackConnection : IScreenLuaConnection
    {
        private readonly ScreenLuaConnection remote = new();
        internal readonly TaskCompletionSource<ScreenLuaResponse> Computed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Callbacks;
        public async Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
        {
            var response = await remote.ExchangeAsync(request, token);
            if (request.Event?.Result?.Action != GameServiceAction.CheckGameRelease) return response;
            Interlocked.Increment(ref Callbacks);
            Computed.TrySetResult(response); // The real callback ran; its effects are still only a packet.
            await Release.Task; // Ignore cancellation deliberately: also exercise a late returning packet.
            Returned.TrySetResult();
            return response;
        }
        public ValueTask DisposeAsync() => remote.DisposeAsync();
    }

    private static async Task Until(ScreenLuaSession session, Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do { Assert.IsFalse(timeout.IsCancellationRequested, string.Join("\n", session.Diagnostics)); session.Poll(); await Task.Delay(1); }
        while (!done());
    }

    [TestMethod]
    [DataRow("duplicate")] [DataRow("lost")] [DataRow("request")] [DataRow("session")]
    [DataRow("generation")] [DataRow("owner")] [DataRow("instance")] [DataRow("version")]
    public async Task ReplayedOutOfOrderOrLostWorkerResponseCannotRepeatNetworkOrApplyCallbackEffects(string failure)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, new("author", "novel", "1.0.0"));
        var connection = new Connection(failure);
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.ConnectReleaseChecks(service);
        session.Attach(instance, """
            return {on_load=function() ui.check_game_release() end,
              on_event=function(e)
                ui.set_text(refs.label,'must not commit')
                ui.save_slot(nil,'Non-idempotent callback')
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await Until(session, () => provider.Calls == 1);
        var before = instance.Definition;
        provider.Reply.SetResult(new(ReleaseCheckCode.NetworkUnavailable));
        await Until(session, () => !session.IsAvailable(instance));
        await session.DisposeAsync();
        Assert.AreSame(before, instance.Definition); Assert.IsEmpty(session.DrainServices());
        Assert.AreEqual(1, provider.Calls); Assert.AreEqual(1, connection.Loads); Assert.AreEqual(1, connection.Results);
        Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, session.ReservedServiceReplies);
        Assert.HasCount(1, session.Issues); Assert.AreEqual(ScreenLuaIssueKind.Conversation, session.Issues[0].Kind);
        // The fallback navigation survives a broken conversation; no automatic worker reload/replay.
        Assert.IsTrue(stack.Route(new(ScreenInputKind.Back)).Consumed); Assert.IsEmpty(stack.Instances);
    }

    [TestMethod]
    public async Task ReusedReleaseRequestIdIsRejectedBeforeEffectsIncludingAfterAcknowledgement()
    {
        var provider = new Provider();
        await using var service = new ReleaseCheckService(Guid.NewGuid(), false, provider, new("author", "novel", "1.0.0"));
        var owner = service.Attach(Guid.NewGuid(), 1); var id = Guid.NewGuid();
        service.Check(owner, requestId: id); service.Poll();
        Assert.Throws<ArgumentException>(() => service.Check(owner, requestId: id));
        provider.Reply.SetResult(new(ReleaseCheckCode.NetworkUnavailable));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.HasRunningTransport) { timeout.Token.ThrowIfCancellationRequested(); service.Poll(); await Task.Delay(1); }
        Assert.IsTrue(service.TryTakeResult(owner, out var result)); Assert.AreEqual(id, result!.RequestId);
        Assert.Throws<ArgumentException>(() => service.Check(owner, requestId: id));
        Assert.IsFalse(service.TryTakeResult(owner, out _)); Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual(0, service.ReservationCount);
    }

    [TestMethod]
    [DataRow("close", false)] [DataRow("close", true)]
    [DataRow("new_game", false)] [DataRow("new_game", true)]
    [DataRow("load", false)] [DataRow("load", true)]
    public async Task LifetimeChangeAfterCallbackExecutionBeforeCommitDiscardsItsEntirePacket(string change, bool hostPrepared)
    {
        var scene = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("ru", "First"), DialogueLine.Create("ru", "Saved")] };
        var project = NovelProject.Create("Callback race") with { Scenes = [scene], StartScreenSceneId = change == "new_game" ? scene.Id : null };
        using var seed = new GameSession(project, scene.Id, "ru");
        if (seed.Phase == GameSessionPhase.Menu) Assert.IsTrue(seed.NewGame(seed.Generation));
        Assert.IsTrue(seed.Player.Next()); var saved = GameSave.Serialize(seed.Player.Execution);
        using var game = new GameSession(project, scene.Id, "ru");
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var provider = new Provider();
        await using var service = new ReleaseCheckService(project.Id, true, provider, new("author", "novel", "1.0.0"));
        var connection = new HeldCallbackConnection(); var firstConnection = true;
        await using var session = new ScreenLuaSession(stack, () =>
        {
            if (!firstConnection) return new ScreenLuaConnection();
            firstConnection = false; return connection;
        });
        void ResetHost() { stack.Clear(); session.Reset(); }
        game.Resetting += ResetHost;
        try
        {
            session.ConnectReleaseChecks(service);
            var refs = ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id);
            session.Attach(instance, """
                return {on_load=function() ui.check_game_release() end,
                  on_event=function(e)
                    ui.set_text(refs.label,'stale callback')
                    ui.set_state({handled=true})
                    ui.save_slot(nil,'Must not create a slot')
                  end}
                """, refs);
            await Until(session, () => provider.Calls == 1);
            var before = instance.Definition; var version = instance.Version; var applied = session.AppliedBatches;
            provider.Reply.SetResult(new(ReleaseCheckCode.NetworkUnavailable));
            await Until(session, () => connection.Computed.Task.IsCompleted);
            var response = await connection.Computed.Task;
            Assert.IsNull(response.Error);
            Assert.IsTrue(response.Changes.Any(c => c.Kind == "text"));
            Assert.IsTrue(response.Changes.Any(c => c.Kind == "state"));
            Assert.IsTrue(response.Changes.Any(c => c.Service?.Action == GameServiceAction.SaveSlot));
            var pending = session.PendingTask!;
            if (hostPrepared)
            {
                connection.Release.TrySetResult();
                await pending.WaitAsync(TimeSpan.FromSeconds(5)); // Ready for Poll, but not committed.
            }
            Assert.AreSame(before, instance.Definition);
            Assert.AreEqual(applied, session.AppliedBatches); Assert.IsEmpty(session.DrainServices());
            Assert.AreEqual(1, service.ReservationCount);
            switch (change)
            {
                case "close": stack.Close(instance); session.Poll(); break;
                case "new_game": Assert.IsTrue(game.NewGame(game.Generation)); break;
                default: game.LoadSave(saved); break;
            }
            Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, session.ReservedServiceReplies);
            connection.Release.TrySetResult();
            await connection.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (change != "close") { }
            var replacement = stack.Open(Guid.NewGuid(), before);
            session.Attach(replacement, "return {on_load=function() ui.set_text(refs.label,'replacement') end,on_event=function() error('unexpected callback') end}", refs);
            await Until(session, () => session.IsSettled(replacement));
            Assert.AreEqual("replacement", replacement.Definition.Nodes[2].Text);
            Assert.AreSame(before, instance.Definition); Assert.AreEqual(version, instance.Version);
            Assert.AreEqual(applied + 1, session.AppliedBatches); Assert.IsEmpty(session.DrainServices());
            Assert.AreEqual(1, provider.Calls); Assert.AreEqual(1, connection.Callbacks);
            Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, session.ReservedServiceReplies);
            if (change == "load") Assert.AreEqual(saved, GameSave.Serialize(game.Player.Execution));
            Assert.IsEmpty(session.Diagnostics); Assert.IsEmpty(game.CleanupDiagnostics);
        }
        finally { connection.Release.TrySetResult(); game.Resetting -= ResetHost; }
    }
}
