using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using NovellMaker.Core.Execution;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Runtime.ExternalServices;

namespace NovellMaker.Runtime.Tests;

/// <summary>10.5: real HTTP adapter, runtime host and isolated Lua worker; no internet dependency.</summary>
[TestClass]
public sealed class ReleaseFailureTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal void Advance(double seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private sealed class Call(CancellationToken token)
    {
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<HttpResponseMessage> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<Call> Calls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.github.com/repos/author/novel/releases/latest", request.RequestUri!.AbsoluteUri);
            var call = new Call(token); Calls.Enqueue(call);
            return call.Reply.Task; // Deliberately ignore cancellation to expose late completions.
        }
    }

    private sealed class StalledBody : Stream
    {
        internal readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Reading.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingStore(GameSlotStore inner, bool failAfterWrite) : IGameSlotStore
    {
        internal int Writes;
        internal string? WrittenId;
        public async Task SaveAsync(string id, GameSlotSnapshot snapshot, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes); WrittenId = id;
            await inner.SaveAsync(id, snapshot, token);
            if (failAfterWrite) throw new IOException("Acknowledgement lost after disk commit");
        }
        public Task<GameSlotRead> ReadAsync(string id, CancellationToken token = default) => inner.ReadAsync(id, token);
        public Task<ImmutableArray<GameSlotInfo>> ListAsync(CancellationToken token = default) => inner.ListAsync(token);
        public Task<GameSlotRead?> LatestAsync(CancellationToken token = default) => inner.LatestAsync(token);
        public Task DeleteAsync(string id, CancellationToken token = default) => inner.DeleteAsync(id, token);
    }

    private sealed class HeldReadStore(GameSlotStore inner, bool failRead) : IGameSlotStore
    {
        internal readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads;
        public async Task<GameSlotRead> ReadAsync(string id, CancellationToken token = default)
        {
            Interlocked.Increment(ref Reads); Reading.TrySetResult();
            await Release.Task.WaitAsync(token);
            if (failRead) throw new IOException("Slot read failed");
            return await inner.ReadAsync(id, token);
        }
        public Task SaveAsync(string id, GameSlotSnapshot snapshot, CancellationToken token = default) => inner.SaveAsync(id, snapshot, token);
        public Task<ImmutableArray<GameSlotInfo>> ListAsync(CancellationToken token = default) => inner.ListAsync(token);
        public Task<GameSlotRead?> LatestAsync(CancellationToken token = default) => inner.LatestAsync(token);
        public Task DeleteAsync(string id, CancellationToken token = default) => inner.DeleteAsync(id, token);
    }

    private const string Source = """
        local count=0
        return {on_load=function() ui.set_text(refs.label,'ready') end,
          on_event=function(e)
            if e.kind=='click' then ui.check_game_release()
            elseif e.kind=='service_result' and e.operation=='CheckGameRelease' then
              count=count+1
              ui.set_text(refs.label,e.code..':'..count)
              ui.log('info',e.request_id..':'..e.code)
            end
          end}
        """;

    private sealed class Rig : IAsyncDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly Handler Handler = new();
        internal readonly GitHubReleaseProvider Provider;
        internal readonly ReleaseCheckService Service;
        internal readonly GameSession Game;
        internal readonly RuntimeScreens Host;
        internal readonly List<GameServiceCommand> Effects = [];
        internal readonly NovelProject Project;
        internal RuntimeGameServices? Services;
        internal ScreenInstance Origin => Host.Stack.Instances.Last();
        internal string Text => Origin.Definition.Nodes[1].Text;

        internal Rig(bool menu = false, string source = Source, bool preview = true)
        {
            var script = new LuaScriptAsset(Guid.NewGuid(), "failures.lua", source);
            NovelScene Scene(string name)
            {
                var scene = ScreenFixture.Create(name).ToScene();
                return scene with { Lines = [DialogueLine.Create("ru", "Still playable"), DialogueLine.Create("ru", "Next line")], Canvas = scene.Canvas with
                {
                    HandlerScriptId = script.Id,
                    References = ImmutableDictionary<string, Guid>.Empty.Add("label", scene.Canvas.Nodes[1].Id),
                    Buttons = [new(scene.Canvas.Nodes[2].Id, ScreenButtonAction.Lua, null)]
                } };
            }
            var story = Scene("Story"); var start = Scene("Menu");
            Project = NovelProject.Create("Failures") with { Scenes = [story, start], Scripts = [script],
                StartScreenSceneId = menu ? start.Id : null,
                ReleaseCheck = new("github_releases_v1", "author", "novel", "1.0.0") };
            Game = new(Project, story.Id, "ru", playbackMode: preview ? PlaybackMode.AuthorPreview : PlaybackMode.Game);
            Provider = new(Handler, Clock);
            Service = new(Project.Id, preview, Provider, Project.ReleaseCheck.ToConfiguration(), Clock);
            Host = new(Guid.NewGuid(), enableLua: true, hostService: (origin, command, generation) =>
            { Effects.Add(command); Services?.Request(origin, command, generation); });
            Host.Configure(Project, () => Game.Player.Execution.State.Variables, () => Game.Generation);
            Host.Scripts!.ConnectReleaseChecks(Service);
            Game.Resetting += Host.Reset;
            Host.SyncScene(Game.Player.Execution.State.SceneId);
        }

        internal void Click() => Assert.AreEqual(ScreenEventAdmission.Accepted,
            Host.Scripts!.EnqueueUsingInstanceSnapshot(Origin, new("click", Origin.Definition.Nodes[2].Id)));

        internal async Task Until(Func<bool> done)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            do
            {
                Assert.IsFalse(timeout.IsCancellationRequested, string.Join("\n", Host.Diagnostics));
                Host.Poll();
                if (Services?.Poll() == true && Services.LastResult is { Success: true, Action: GameServiceAction.LoadSlot or GameServiceAction.ContinueGame })
                    Host.SyncScene(Game.Player.Execution.State.SceneId, afterLoad: true);
                await Task.Delay(1);
            } while (!done());
        }
        internal Task Drain() => Until(() => !Host.HasPending);
        internal async Task<Call> Start()
        {
            await Drain(); Click(); await Until(() => Handler.Calls.Count == 1);
            return Handler.Calls.Single();
        }
        public async ValueTask DisposeAsync()
        {
            Game.Resetting -= Host.Reset;
            await Host.DisposeAsync();
            // Settle uncooperative test tasks even on assertion failure.
            foreach (var call in Handler.Calls) call.Reply.TrySetCanceled();
            await Service.DisposeAsync(); Provider.Dispose(); await Game.DisposeAsync();
        }
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"tag_name":"v1.1.0","draft":false,"prerelease":false,"html_url":"https://github.com/author/novel/releases/tag/v1.1.0"}
            """, Encoding.UTF8, "application/json")
    };

    [TestMethod]
    [DataRow(HttpRequestError.NameResolutionError, "NetworkUnavailable")]
    [DataRow(HttpRequestError.ConnectionError, "NetworkUnavailable")]
    [DataRow(HttpRequestError.SecureConnectionError, "TlsFailure")]
    public async Task OfflineAndTlsReachRealWorkerOnceWithoutRetriesOrStoppingStory(HttpRequestError error, string code)
    {
        await using var rig = new Rig(); var call = await rig.Start();
        call.Reply.SetException(new HttpRequestException(error, "PRIVATE-TRANSPORT-DETAILS"));
        await rig.Until(() => rig.Text == code + ":1");
        // Elapsed cooldown/deadline alone must never trigger a retry.
        rig.Clock.Advance(1000); await rig.Drain();
        Assert.HasCount(1, rig.Handler.Calls); Assert.AreEqual(code + ":1", rig.Text);
        Assert.AreEqual(0, rig.Service.ReservationCount); Assert.AreEqual(0, rig.Host.Scripts!.ReservedServiceReplies);
        Assert.IsFalse(rig.Host.Scripts.Journal.Entries.Any(e => e.Data.Message.Contains("PRIVATE-TRANSPORT-DETAILS", StringComparison.Ordinal)));
        Assert.IsTrue(rig.Game.Player.Next()); Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task DeadlineKeepsOneTerminalResultAndDiscardsLateSuccessEvenDuringPause(bool paused)
    {
        await using var rig = new Rig(); var call = await rig.Start(); var origin = rig.Origin;
        ScreenInstance? overlay = paused ? rig.Host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Cover").Definition()) : null;
        rig.Host.Poll(); rig.Clock.Advance(10); rig.Host.Poll();
        await rig.Until(() => call.Token.IsCancellationRequested);
        if (paused)
        {
            Assert.AreEqual(1, rig.Service.ReservationCount); Assert.IsTrue(rig.Host.Scripts!.HasReleaseChecks(origin));
            Assert.AreEqual("ready", origin.Definition.Nodes[1].Text);
            rig.Host.Stack.Close(overlay!);
        }
        await rig.Until(() => rig.Text == "Timeout:1");
        call.Reply.SetResult(Success()); await rig.Until(() => !rig.Service.HasRunningTransport); await rig.Drain();
        Assert.AreEqual("Timeout:1", rig.Text); Assert.HasCount(1, rig.Handler.Calls);
        // A discarded success must not seed the profile cache.
        rig.Click(); await rig.Until(() => rig.Text == "RateLimited:2");
        Assert.HasCount(1, rig.Handler.Calls); Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task BusyResultCanPrecedeOriginalAndRepeatedPollsCannotDuplicateEither(bool preview)
    {
        await using var rig = new Rig(preview: preview); var call = await rig.Start();
        rig.Click(); await rig.Until(() => rig.Text == "Busy:1");
        Assert.IsTrue(rig.Host.Scripts!.HasReleaseChecks(rig.Origin));
        call.Reply.SetResult(Success()); await rig.Until(() => rig.Text == "Ok:2");
        for (var i = 0; i < 50; i++) rig.Host.Poll();
        await rig.Drain(); Assert.AreEqual("Ok:2", rig.Text); Assert.HasCount(1, rig.Handler.Calls);
        Assert.AreEqual(0, rig.Service.ReservationCount); Assert.AreEqual(0, rig.Host.Scripts.ReservedServiceReplies);
        Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    public async Task TimeoutWhileReadingBodyDisposesStreamAndDoesNotRetry()
    {
        await using var rig = new Rig(); var call = await rig.Start();
        var body = new StalledBody(); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new("application/json"); call.Reply.SetResult(response);
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Advance(10); await rig.Until(() => rig.Text == "Timeout:1" && !rig.Service.HasRunningTransport);
        Assert.IsTrue(body.Disposed); Assert.HasCount(1, rig.Handler.Calls);
        rig.Clock.Advance(1000); await rig.Drain(); Assert.HasCount(1, rig.Handler.Calls);
        Assert.AreEqual(0, rig.Service.ReservationCount); Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    [DataRow("close", false, false)] [DataRow("close", true, false)] [DataRow("close", false, true)]
    [DataRow("new_game", false, false)] [DataRow("new_game", true, false)] [DataRow("new_game", false, true)]
    [DataRow("load", false, false)] [DataRow("load", true, false)] [DataRow("load", false, true)]
    public async Task LifetimeChangeDropsPendingOrAlreadyCompletedResponseAndNeverReplays(string change, bool completed, bool fault)
    {
        await using var rig = new Rig(menu: change == "new_game");
        using var seed = new GameSession(rig.Project, rig.Project.Scenes[0].Id, "ru");
        if (seed.Phase == GameSessionPhase.Menu) seed.NewGame(seed.Generation);
        var slot = GameSave.Serialize(seed.Player.Execution);
        var call = await rig.Start(); var old = rig.Origin; var before = old.Definition;
        if (completed)
        {
            // Publish the HTTP result but hold callback delivery, without relying on scheduler sleeps.
            rig.Host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Cover").Definition()); rig.Host.Poll();
            call.Reply.SetResult(Success()); await rig.Until(() => !rig.Service.HasRunningTransport);
            Assert.IsTrue(rig.Host.Scripts!.HasReleaseChecks(old));
        }
        switch (change)
        {
            case "close":
                rig.Host.Stack.Close(old); rig.Host.Poll();
                rig.Host.SyncScene(rig.Project.Scenes[1].Id);
                break;
            case "new_game":
                Assert.IsTrue(rig.Game.NewGame(rig.Game.Generation));
                rig.Host.SyncScene(rig.Game.Player.Execution.State.SceneId);
                break;
            default:
                rig.Game.LoadSave(slot);
                rig.Host.SyncScene(rig.Game.Player.Execution.State.SceneId, afterLoad: true);
                break;
        }
        if (!completed)
        {
            if (fault) call.Reply.SetException(new HttpRequestException(HttpRequestError.ConnectionError, "Late transport fault"));
            else call.Reply.SetResult(Success());
        }
        await rig.Until(() => !rig.Service.HasRunningTransport); await rig.Drain();
        Assert.AreSame(before, old.Definition); Assert.AreNotSame(old, rig.Origin);
        Assert.HasCount(1, rig.Handler.Calls); Assert.AreEqual(0, rig.Service.ReservationCount);
        Assert.AreEqual(0, rig.Host.Scripts!.ReservedServiceReplies);
        Assert.IsFalse(rig.Host.Scripts.Journal.Entries.Any(e => e.Data.Message.EndsWith(":Ok", StringComparison.Ordinal)));
        if (change == "load") Assert.AreEqual(slot, GameSave.Serialize(rig.Game.Player.Execution));
        Assert.IsEmpty(rig.Game.CleanupDiagnostics); Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    public async Task DifferentOwnersReceiveOnlyTheirOwnResultsInCompletionOrder()
    {
        await using var rig = new Rig(); var call = await rig.Start(); var first = rig.Origin;
        var asset = rig.Project.Scenes[1].AsScreen();
        var second = rig.Host.Stack.Open(Guid.NewGuid(), asset.Definition(), new(false, false));
        rig.Host.Scripts!.Attach(second, Source, asset.References); await rig.Drain();
        rig.Click(); await rig.Until(() => second.Definition.Nodes[1].Text == "RateLimited:1");
        Assert.AreEqual("ready", first.Definition.Nodes[1].Text);
        call.Reply.SetResult(Success()); await rig.Until(() => first.Definition.Nodes[1].Text == "Ok:1");
        await rig.Drain(); Assert.AreEqual("RateLimited:1", second.Definition.Nodes[1].Text);
        Assert.AreEqual(0, rig.Service.ReservationCount); Assert.AreEqual(0, rig.Host.Scripts.ReservedServiceReplies);
        Assert.HasCount(1, rig.Handler.Calls); Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    public async Task FailedSlotLoadPreservesOwnerAndPendingHttpThenDeliversOnce()
    {
        await using var rig = new Rig(); var call = await rig.Start();
        var old = rig.Origin; var player = rig.Game.Player; var generation = rig.Game.Generation;
        Assert.Throws<ArgumentException>(() => rig.Game.LoadSave("{}"));
        rig.Host.Poll(); Assert.AreSame(old, rig.Origin); Assert.AreSame(player, rig.Game.Player);
        Assert.AreEqual(generation, rig.Game.Generation); Assert.IsFalse(call.Token.IsCancellationRequested);
        call.Reply.SetResult(Success()); await rig.Until(() => rig.Text == "Ok:1"); await rig.Drain();
        Assert.HasCount(1, rig.Handler.Calls); Assert.AreEqual(0, rig.Service.ReservationCount);
        Assert.IsEmpty(rig.Host.Diagnostics);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task NonIdempotentSaveFromCallbackIsNotRepeatedByDuplicateReplyOrSlotLoad(bool failAfterWrite)
    {
        var source = Source.Replace("ui.log('info',e.request_id..':'..e.code)", "ui.save_slot(nil,'External result')", StringComparison.Ordinal);
        await using var rig = new Rig(source: source); var call = await rig.Start();
        var path = Path.Combine(TestPaths.TempDirectory, "release-failures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CountingStore(new(rig.Project, Path.Combine(path, "slots"), Path.Combine(path, "quick.json")), failAfterWrite);
            await using var slots = new GameSessionSlots(rig.Game, store);
            using var services = new RuntimeGameServices(rig.Game, slots);
            rig.Services = services;
            services.IsCurrentOrigin = origin => rig.Host.Stack.Instances.Any(i => ReferenceEquals(i, origin));
            GameServiceResult? delivered = null;
            services.Completed += (origin, result) =>
            { delivered = result; Assert.IsTrue(rig.Host.DeliverServiceResult(origin, result)); };
            call.Reply.SetResult(Success()); await rig.Until(() => delivered is not null);
            var command = rig.Effects.Single(); Assert.AreEqual(GameServiceAction.SaveSlot, command.Action);
            // A generated ID is observably non-idempotent: a repeated command would create a second slot.
            Assert.IsNull(command.SlotId);
            var reply = delivered!; Assert.AreEqual(!failAfterWrite, reply.Success);
            Assert.IsFalse(rig.Host.DeliverServiceResult(rig.Origin, reply));
            await rig.Drain();
            Assert.IsFalse(rig.Host.DeliverServiceResult(rig.Origin, reply));
            rig.Clock.Advance(1000); await rig.Drain();
            var saved = await store.ReadAsync(store.WrittenId!); Assert.IsNotNull(saved.Snapshot);
            Assert.IsFalse(saved.Snapshot.SaveJson.Contains("CheckGameRelease", StringComparison.Ordinal));
            Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.LoadSlot, store.WrittenId), rig.Game.Generation));
            await rig.Until(() => !slots.IsBusy); await rig.Drain();
            Assert.HasCount(1, rig.Effects); Assert.HasCount(1, await store.ListAsync()); Assert.HasCount(1, rig.Handler.Calls);
            Assert.AreEqual(1, store.Writes);
            Assert.IsFalse(rig.Host.DeliverServiceResult(rig.Origin, reply));
            Assert.IsEmpty(rig.Host.Diagnostics);
            rig.Services = null;
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [TestMethod]
    [DataRow(false, false)] [DataRow(false, true)]
    [DataRow(true, false)] [DataRow(true, true)]
    public async Task HttpCompletionDuringAsyncSlotReadRespectsLoadCommitAndNeverReplays(bool failRead, bool deliverBeforeLoad)
    {
        await using var rig = new Rig();
        var path = Path.Combine(TestPaths.TempDirectory, "release-slot-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var disk = new GameSlotStore(rig.Project, Path.Combine(path, "slots"), Path.Combine(path, "quick.json"));
            using var seed = new GameSession(rig.Project, rig.Project.Scenes[0].Id, "ru");
            Assert.IsTrue(seed.Player.Next());
            var snapshot = GameSlotStore.Prepare(GameSlotStore.Capture(seed));
            await disk.SaveAsync("saved", snapshot);
            var store = new HeldReadStore(disk, failRead);
            await using var slots = new GameSessionSlots(rig.Game, store);
            using var services = new RuntimeGameServices(rig.Game, slots);
            rig.Services = services;
            services.IsCurrentOrigin = origin => rig.Host.Stack.Instances.Any(i => ReferenceEquals(i, origin));
            var replies = new List<GameServiceResult>();
            services.Completed += (origin, result) =>
            { replies.Add(result); Assert.IsTrue(rig.Host.DeliverServiceResult(origin, result)); };
            var call = await rig.Start(); var old = rig.Origin; var player = rig.Game.Player;
            var state = player.Execution.State; var generation = rig.Game.Generation;
            var load = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.LoadSlot, "saved");
            Assert.AreEqual(ScreenEventAdmission.Accepted, rig.Host.Scripts!.ReserveServiceReply(old, load));
            Assert.IsTrue(services.Request(old, load, generation));
            await store.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsTrue(slots.IsBusy);
            var overlay = deliverBeforeLoad ? null : rig.Host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Cover").Definition());
            rig.Host.Poll(); call.Reply.SetResult(Success());
            await rig.Until(() => !rig.Service.HasRunningTransport);
            if (deliverBeforeLoad) await rig.Until(() => old.Definition.Nodes[1].Text == "Ok:1");
            else
            {
                Assert.AreEqual("ready", old.Definition.Nodes[1].Text);
                Assert.IsTrue(rig.Host.Scripts.HasReleaseChecks(old)); Assert.AreEqual(1, rig.Service.ReservationCount);
            }
            // HTTP has finished while the asynchronous read is still held. No load has committed.
            Assert.IsTrue(slots.IsBusy); Assert.AreEqual(1, store.Reads); Assert.IsEmpty(replies);
            Assert.AreSame(player, rig.Game.Player); Assert.AreSame(state, player.Execution.State);
            Assert.AreEqual(generation, rig.Game.Generation);
            store.Release.TrySetResult(); await rig.Until(() => !slots.IsBusy);
            Assert.IsNotNull(services.LastResult); Assert.AreEqual(!failRead, services.LastResult.Success);
            if (failRead)
            {
                Assert.AreEqual(generation, rig.Game.Generation); Assert.AreSame(player, rig.Game.Player);
                Assert.AreSame(state, player.Execution.State); Assert.IsTrue(rig.Host.Scripts.IsAvailable(old));
                Assert.HasCount(1, replies); Assert.AreEqual(load.RequestId, replies[0].RequestId);
                if (overlay is not null) rig.Host.Stack.Close(overlay);
                await rig.Until(() => old.Definition.Nodes[1].Text == "Ok:1");
            }
            else
            {
                Assert.AreEqual(generation + 1, rig.Game.Generation); Assert.AreNotSame(player, rig.Game.Player);
                Assert.AreNotSame(old, rig.Origin); Assert.IsEmpty(replies);
                Assert.AreEqual(snapshot.SaveJson, GameSave.Serialize(rig.Game.Player.Execution));
            }
            await rig.Drain();
            var httpCallbacks = rig.Host.Scripts.Journal.Entries.Where(e => e.Data.Message.EndsWith(":Ok", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(failRead || deliverBeforeLoad ? 1 : 0, httpCallbacks);
            if (httpCallbacks.Length != 0) Assert.AreEqual(old.Id, httpCallbacks[0].InstanceId);
            Assert.HasCount(1, rig.Handler.Calls); Assert.AreEqual(1, store.Reads); Assert.IsEmpty(rig.Effects);
            Assert.HasCount(1, await disk.ListAsync());
            Assert.AreEqual(0, rig.Service.ReservationCount); Assert.AreEqual(0, rig.Host.Scripts.ReservedServiceReplies);
            Assert.IsEmpty(rig.Host.Diagnostics); Assert.IsEmpty(rig.Game.CleanupDiagnostics);
            rig.Services = null;
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
