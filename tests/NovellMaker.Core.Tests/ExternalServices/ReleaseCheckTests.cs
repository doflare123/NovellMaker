using Microsoft.VisualStudio.TestTools.UnitTesting;
using NovellMaker.Core.ExternalServices;

namespace NovellMaker.Core.Tests.ExternalServices;

[TestClass]
public sealed class ReleaseCheckTests
{
    private static readonly ReleaseCheckConfiguration Config = new("writer", "novel", "1.9.0");
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        internal void Advance(double seconds)
        {
            Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
            Timer[] ready;
            lock (timers) { ready = timers.Where(t => !t.Disposed && t.Due <= GetTimestamp()).ToArray(); foreach (var timer in ready) timer.Disposed = true; }
            foreach (var timer in ready) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (timers) { var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal long Due;
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.timers) { Due = owner.GetTimestamp() + dueTime.Ticks; return !Disposed; } }
            public void Dispose() { lock (owner.timers) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Provider : IReleaseCheckProvider
    {
        internal readonly TaskCompletionSource<ReleaseProviderReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int Calls;
        public Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration configuration, CancellationToken cancellationToken)
        {
            Token = cancellationToken; Interlocked.Increment(ref Calls); Started.TrySetResult(); return Reply.Task;
        }
    }
    private sealed class Immediate(Func<ReleaseCheckConfiguration, CancellationToken, Task<ReleaseProviderReply>> callback) : IReleaseCheckProvider
    {
        public Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration config, CancellationToken token) => callback(config, token);
    }
    private static ReleaseCheckData Data(bool update = true, string latest = "1.10.0.0") => new(
        "1.9.0.0", latest, update, "https://github.com/writer/novel/releases/tag/v" + latest, DateTimeOffset.UnixEpoch);
    private static ReleaseCheckService Service(IReleaseCheckProvider provider, Clock? clock = null, ReleaseCheckConfiguration? config = null) =>
        new(Guid.NewGuid(), false, provider, config ?? Config, clock);
    private static ReleaseCheckService.Owner Attach(ReleaseCheckService service, long generation = 1) => service.Attach(Guid.NewGuid(), generation);
    private static ReleaseCheckResult Take(ReleaseCheckService service, ReleaseCheckService.Owner owner)
    {
        Assert.IsTrue(service.TryTakeResult(owner, out var result)); return result!;
    }
    private static async Task Finish(ReleaseCheckService service)
    {
        for (var i = 0; i < 3000 && service.HasRunningTransport; i++) { service.Poll(); await Task.Delay(1); }
        Assert.IsFalse(service.HasRunningTransport, "Transport did not finish");
    }

    [TestMethod]
    [DataRow("0.0.0", "0.0.0.0")]
    [DataRow("65535.65535.65535.65535", "65535.65535.65535.65535")]
    [DataRow("1.10.2.3", "1.10.2.3")]
    public void VersionsNormalize(string input, string normalized)
    {
        Assert.IsTrue(ReleaseVersion.TryParse(input, out var result)); Assert.AreEqual(normalized, result.ToString());
        Assert.IsTrue(ReleaseVersion.TryParse("v" + input, out var tag, tag: true)); Assert.AreEqual(result, tag);
        Assert.IsTrue(new ReleaseVersion(1, 10, 0, 0).CompareTo(new(1, 9, 9, 9)) > 0);
    }

    [TestMethod]
    [DataRow(null)] [DataRow("")] [DataRow("1.0")] [DataRow("1.0.0.0.0")] [DataRow("v1.0.0")]
    [DataRow("01.0.0")] [DataRow("1..0")] [DataRow("65536.0.0")] [DataRow("-1.0.0")]
    [DataRow("1.0.0-beta")] [DataRow("1.0.0 ")] [DataRow("１.0.0")]
    public void VersionsReject(string? value) => Assert.IsFalse(ReleaseVersion.TryParse(value, out _));

    [TestMethod]
    [DataRow("-writer", "novel")] [DataRow("writer-", "novel")] [DataRow("a--b", "novel")]
    [DataRow("a/b", "novel")] [DataRow("writer", "../novel")] [DataRow("writer", "novel.git")]
    [DataRow("writer", ".")] [DataRow("writer", "..")] [DataRow("", "novel")] [DataRow("writer", "")]
    public void ConfigurationRejectsPaths(string owner, string repo) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ReleaseCheckConfiguration(owner, repo, "1.0.0"));

    [TestMethod]
    [DataRow("http://github.com/writer/novel/releases/tag/v1.10.0")]
    [DataRow("https://evil.test/writer/novel/releases/tag/v1.10.0")]
    [DataRow("https://github.com:444/writer/novel/releases/tag/v1.10.0")]
    [DataRow("https://user@github.com/writer/novel/releases/tag/v1.10.0")]
    [DataRow("https://github.com/writer/other/releases/tag/v1.10.0")]
    [DataRow("https://github.com/writer/novel/releases/tag/../v1.10.0")]
    [DataRow("https://github.com/writer/novel/releases/tag/v1.10.0?token=x")]
    [DataRow("https://github.com/writer/novel/releases/tag/v1.10.0#x")]
    [DataRow("https://github.com/writer/novel/releases/tag/v1.10.0%2f")]
    [DataRow("https://github.com/writer/novel/releases/tag/v1.10.0%255c")]
    public void PageRejectsEscapes(string url) => Assert.IsFalse(Config.IsValidPage(url, new(1, 10, 0, 0)));

    [TestMethod]
    public async Task DispatchReservesCooldownAndDrainsQueuedRequests()
    {
        var provider = new Provider(); var clock = new Clock(); using var service = Service(provider, clock);
        var owners = Enumerable.Range(0, 6).Select(_ => Attach(service)).ToArray();
        var ids = owners.Select(o => service.Check(o)).ToArray();
        Assert.AreEqual(4, service.WaitingCount);
        Assert.AreEqual(ReleaseCheckCode.Busy, Take(service, owners[5]).Code);
        service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, provider.Calls); Assert.AreEqual(0, service.WaitingCount);
        foreach (var owner in owners.Skip(1).Take(4))
        {
            var reply = Take(service, owner); Assert.AreEqual(ReleaseCheckCode.RateLimited, reply.Code); Assert.AreEqual(60, reply.RetryAfterSeconds);
        }
        provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data())); await Finish(service);
        var success = Take(service, owners[0]); Assert.AreEqual(ids[0], success.RequestId); Assert.IsTrue(success.Success);
        service.Check(owners[1]); Assert.IsTrue(Take(service, owners[1]).Release!.FromCache);
        clock.Advance(900); service.Check(owners[1]); service.Poll(); await Finish(service);
        Assert.IsTrue(Take(service, owners[1]).Success); Assert.AreEqual(2, provider.Calls);
    }

    [TestMethod]
    public async Task CompletedReplyDoesNotApplyBeforePollAndBusyDoesNotReplaceIt()
    {
        var provider = new Provider(); using var service = Service(provider); var owner = Attach(service);
        var first = service.Check(owner); var busy = service.Check(owner);
        Assert.AreEqual(busy, Take(service, owner).RequestId); Assert.IsTrue(service.IsBusy(owner));
        service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data()));
        Assert.IsFalse(service.TryTakeResult(owner, out _));
        await Finish(service); Assert.IsTrue(service.IsBusy(owner));
        var result = Take(service, owner); Assert.AreEqual(first, result.RequestId); Assert.IsFalse(service.IsBusy(owner));
        Assert.IsFalse(service.TryTakeResult(owner, out _));
        service.Check(owner); var cachedBusy = service.Check(owner);
        Assert.IsTrue(Take(service, owner).Release!.FromCache); Assert.AreEqual(cachedBusy, Take(service, owner).RequestId);
    }

    [TestMethod]
    public void DeadlineBeforeDispatchWinsAndNeverStartsTransport()
    {
        var provider = new Provider(); var clock = new Clock(); using var service = Service(provider, clock); var owner = Attach(service);
        service.Check(owner); clock.Advance(10); service.Poll();
        Assert.AreEqual(ReleaseCheckCode.Timeout, Take(service, owner).Code); Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task TimeoutKeepsPhysicalSlotUntilIgnoringProviderFinishesAndDiscardsLateReply()
    {
        var provider = new Provider(); var clock = new Clock(); using var service = Service(provider, clock); var owner = Attach(service);
        service.Check(owner); service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(10); service.Poll(); Assert.AreEqual(ReleaseCheckCode.Timeout, Take(service, owner).Code);
        Assert.IsTrue(provider.Token.IsCancellationRequested); Assert.IsTrue(service.HasRunningTransport);
        clock.Advance(51); var second = Attach(service); service.Check(second); clock.Advance(10); service.Poll();
        Assert.AreEqual(ReleaseCheckCode.Timeout, Take(service, second).Code);
        provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data())); await Finish(service);
        Assert.IsFalse(service.TryTakeResult(owner, out _));
        service.Check(owner); service.Poll(); await Finish(service); Assert.IsFalse(Take(service, owner).Release!.FromCache);
    }

    [TestMethod]
    [DataRow(0, 31, 32)] [DataRow(31, 0, 32)] [DataRow(7, 19, 20)]
    public void CancelUsesMaximumDepthAndOriginalOrigin(int startDepth, int cancelDepth, int resultDepth)
    {
        using var service = Service(new Provider()); var owner = Attach(service); var origin = Guid.NewGuid();
        var id = service.Check(owner, startDepth, origin); service.ValidateCancel(owner, id, cancelDepth);
        service.Cancel(owner, id, cancelDepth); service.Poll();
        var result = Take(service, owner); Assert.AreEqual(ReleaseCheckCode.Cancelled, result.Code);
        Assert.AreEqual(origin, result.Origin); Assert.AreEqual(resultDepth, result.Depth); Assert.AreEqual(id, result.RequestId);
        service.Cancel(owner, id, 32); Assert.IsFalse(service.TryTakeResult(owner, out _));
    }

    [TestMethod]
    public void InvalidCancellationHasNoEffectAndForeignIdsAreRejected()
    {
        using var service = Service(new Provider()); var owner = Attach(service); var id = service.Check(owner);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Cancel(owner, id, 32));
        Assert.IsTrue(service.IsBusy(owner)); Assert.IsFalse(service.TryTakeResult(owner, out _));
        Assert.ThrowsExactly<ArgumentException>(() => service.Cancel(Attach(service), id));
        Assert.ThrowsExactly<ArgumentException>(() => service.Cancel(owner, Guid.NewGuid()));
        service.Cancel(owner, id); Assert.AreEqual(ReleaseCheckCode.Cancelled, Take(service, owner).Code);
    }

    [TestMethod]
    public async Task CancelBeforePollBeatsReadyReplyAndNeverPopulatesCache()
    {
        var provider = new Provider(); using var service = Service(provider); var owner = Attach(service);
        var id = service.Check(owner); service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data())); service.Cancel(owner, id, 3);
        Assert.AreEqual(ReleaseCheckCode.Cancelled, Take(service, owner).Code); await Finish(service);
        service.Check(owner); Assert.AreEqual(ReleaseCheckCode.RateLimited, Take(service, owner).Code);
    }

    [TestMethod]
    public async Task PublishedReplyBeatsCancelAndFailedTransportCannotLeakExceptionText()
    {
        var provider = new Provider(); using var service = Service(provider); var owner = Attach(service);
        var id = service.Check(owner); service.Poll(); provider.Reply.SetResult(new(ReleaseCheckCode.NotFound)); await Finish(service);
        service.Cancel(owner, id, 32); Assert.AreEqual(ReleaseCheckCode.NotFound, Take(service, owner).Code);
        using var failing = Service(new Immediate((_, _) => throw new Exception("secret-token")));
        var other = Attach(failing); failing.Check(other); failing.Poll(); await Finish(failing);
        var result = Take(failing, other); Assert.AreEqual(ReleaseCheckCode.InvalidResponse, result.Code);
        Assert.IsFalse(result.Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ResetClosesOldOriginsButKeepsProfileCache()
    {
        var provider = new Provider(); using var service = Service(provider); var owner = Attach(service);
        service.Check(owner); service.Poll(); provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data())); await Finish(service);
        Take(service, owner); service.CloseGeneration(1);
        Assert.ThrowsExactly<ArgumentException>(() => service.Check(owner));
        var next = Attach(service, 2); service.Check(next); Assert.IsTrue(Take(service, next).Release!.FromCache);
        Assert.AreEqual(0, service.ReservationCount);
    }

    [TestMethod]
    public async Task CloseDiscardsPendingAndLateFaultAndConfigurePreservesCooldown()
    {
        var provider = new Provider(); using var service = Service(provider); var owner = Attach(service);
        service.Check(owner); service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.Close(owner); service.Close(owner); Assert.IsTrue(provider.Token.IsCancellationRequested);
        Assert.AreEqual(0, service.ReservationCount);
        provider.Reply.SetException(new Exception("late")); await Finish(service);
        service.Configure(new("other", "game", "1.0.0")); var next = Attach(service);
        service.Check(next); Assert.AreEqual(ReleaseCheckCode.RateLimited, Take(service, next).Code);
        service.Configure(null); next = Attach(service); service.Check(next); Assert.AreEqual(ReleaseCheckCode.Disabled, Take(service, next).Code);
    }

    [TestMethod]
    public async Task RateLimitExtendsCooldownAndDoesNotCacheFailure()
    {
        var provider = new Provider(); var clock = new Clock(); using var service = Service(provider, clock); var owner = Attach(service);
        service.Check(owner); service.Poll(); clock.Advance(2);
        provider.Reply.SetResult(new(ReleaseCheckCode.RateLimited, RetryAfterSeconds: 500)); await Finish(service);
        Assert.AreEqual(500, Take(service, owner).RetryAfterSeconds);
        clock.Advance(100); service.Check(owner); Assert.AreEqual(400, Take(service, owner).RetryAfterSeconds);
        Assert.AreEqual(1, provider.Calls);
    }

    [TestMethod]
    [DataRow(ReleaseCheckCode.NetworkUnavailable)] [DataRow(ReleaseCheckCode.TlsFailure)] [DataRow(ReleaseCheckCode.AccessDenied)]
    [DataRow(ReleaseCheckCode.ProviderUnavailable)] [DataRow(ReleaseCheckCode.InvalidResponse)] [DataRow(ReleaseCheckCode.ResponseTooLarge)]
    [DataRow(ReleaseCheckCode.InvalidVersion)] [DataRow(ReleaseCheckCode.Timeout)]
    public async Task ErrorResultsAreBoundedAndCorrelated(ReleaseCheckCode code)
    {
        using var service = Service(new Immediate((_, _) => Task.FromResult(new ReleaseProviderReply(code)))); var owner = Attach(service);
        var id = service.Check(owner); service.Poll(); await Finish(service); var result = Take(service, owner);
        Assert.AreEqual(code, result.Code); Assert.AreEqual(id, result.RequestId); Assert.IsFalse(result.Success);
        Assert.IsNull(result.Release); Assert.IsTrue(result.Message.Length <= 512); Assert.AreEqual("CheckGameRelease", result.Operation);
        Assert.AreEqual("github_releases_v1", result.Provider);
    }

    [TestMethod]
    public async Task AdapterRepliesAreValidatedBeforeCacheOrDelivery()
    {
        ReleaseProviderReply[] bad = [new((ReleaseCheckCode)999), new(ReleaseCheckCode.Busy), new(ReleaseCheckCode.Disabled),
            new(ReleaseCheckCode.Cancelled), new(ReleaseCheckCode.Ok), new(ReleaseCheckCode.NotFound, Data()),
            new(ReleaseCheckCode.Ok, Data() with { FromCache = true }), new(ReleaseCheckCode.Ok, Data() with { UpdateAvailable = false }),
            new(ReleaseCheckCode.Ok, Data() with { CurrentVersion = "2.0.0.0" }),
            new(ReleaseCheckCode.Ok, Data() with { LatestVersion = "bad" }),
            new(ReleaseCheckCode.Ok, Data() with { PageUrl = "https://evil.test" }),
            new(ReleaseCheckCode.RateLimited, RetryAfterSeconds: 0), null!];
        foreach (var reply in bad)
        {
            using var service = Service(new Immediate((_, _) => Task.FromResult(reply))); var owner = Attach(service);
            service.Check(owner); service.Poll(); await Finish(service);
            Assert.AreEqual(ReleaseCheckCode.InvalidResponse, Take(service, owner).Code);
        }
    }

    [TestMethod]
    public void ReservationAndHistoryBoundsPrecedeEffectsAndOwnersCannotBeForged()
    {
        using var service = new ReleaseCheckService(Guid.NewGuid(), true, new Provider()); var owner = Attach(service);
        for (var i = 0; i < 128; i++) service.Check(owner);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Check(owner)); Assert.AreEqual(128, service.ReservationCount);
        var other = Attach(service); Assert.ThrowsExactly<InvalidOperationException>(() => service.Check(other));
        for (var i = 0; i < 128; i++) Assert.AreEqual(ReleaseCheckCode.Disabled, Take(service, owner).Code);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Check(owner)); // Taking replies does not replenish ID history.
        Assert.AreEqual(0, service.ReservationCount); Assert.IsTrue(service.IsPreview); Assert.AreNotEqual(Guid.Empty, service.ProjectId);
        using var separate = Service(new Provider());
        Assert.ThrowsExactly<ArgumentException>(() => separate.Check(other));
        service.Close(owner); Assert.ThrowsExactly<ArgumentException>(() => service.IsBusy(owner));
        Assert.ThrowsExactly<ArgumentException>(() => service.Check(other, 32));
        Assert.ThrowsExactly<ArgumentException>(() => service.Check(other, origin: Guid.Empty));
    }

    [TestMethod]
    public async Task CancellationOfCooperativeProviderAndDisposeAreIdempotent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Immediate(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return null!; });
        var service = Service(provider); var owner = Attach(service); var id = service.Check(owner); service.Poll();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); service.Cancel(owner, id);
        Assert.AreEqual(ReleaseCheckCode.Cancelled, Take(service, owner).Code); await Finish(service);
        await service.DisposeAsync(); service.Dispose(); service.Poll();
        Assert.ThrowsExactly<ObjectDisposedException>(() => Attach(service));
    }

    [TestMethod]
    public async Task BlockingAdapterAndCancellationCallbackNeverRunOnHost()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbacksRelease = new ManualResetEventSlim();
        var callbacksStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Immediate((_, token) =>
        {
            token.Register(() => { callbacksStarted.TrySetResult(); callbacksRelease.Wait(TimeSpan.FromSeconds(10)); });
            started.SetResult(); release.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(new ReleaseProviderReply(ReleaseCheckCode.NotFound));
        });
        using var service = Service(provider); var owner = Attach(service); var id = service.Check(owner);
        try
        {
            var poll = Task.Run(service.Poll);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await poll.WaitAsync(TimeSpan.FromSeconds(2)); // Must return while the provider is still blocked on release.
            var cancel = Task.Run(() => service.Cancel(owner, id));
            await callbacksStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancel.WaitAsync(TimeSpan.FromSeconds(2)); // Must return while the token callback is still blocked.
            Assert.AreEqual(ReleaseCheckCode.Cancelled, Take(service, owner).Code);
        }
        finally { callbacksRelease.Set(); release.Set(); }
        await Finish(service);
    }

    [TestMethod]
    public async Task TimerUsesRemainingDeadlineAndCancelsWithoutAnotherPoll()
    {
        var clock = new Clock(); var provider = new Provider(); using var service = Service(provider, clock); var owner = Attach(service);
        service.Check(owner); clock.Advance(6); service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(3.9); Assert.IsFalse(provider.Token.IsCancellationRequested);
        clock.Advance(0.1); Assert.IsTrue(provider.Token.IsCancellationRequested);
        Assert.IsFalse(service.TryTakeResult(owner, out _)); // Timer only cancels transport; publication still belongs to Poll.
        service.Poll(); Assert.AreEqual(ReleaseCheckCode.Timeout, Take(service, owner).Code);
        provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data())); await Finish(service);
    }

    [TestMethod]
    public async Task DisposeBoundsIgnoringProviderAndObservesLateFailure()
    {
        var provider = new Provider(); var service = Service(provider); var owner = Attach(service);
        service.Check(owner); service.Poll(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(provider.Token.IsCancellationRequested); Assert.AreEqual(0, service.ReservationCount);
        provider.Reply.SetException(new Exception("late-shutdown-failure"));
        await service.DisposeAsync(); service.Close(owner);
    }

    [TestMethod]
    public void OwnersAndConfigurationHaveExplicitBounds()
    {
        using var service = Service(new Provider());
        Assert.ThrowsExactly<ArgumentException>(() => service.Attach(Guid.Empty, 1));
        Assert.ThrowsExactly<ArgumentException>(() => service.Attach(Guid.NewGuid(), -1));
        var owner = Attach(service);
        Assert.ThrowsExactly<ArgumentException>(() => service.Attach(owner.InstanceId, 1));
        Assert.AreEqual(1L, owner.Generation);
        for (var i = 1; i < ReleaseCheckService.MaximumOwners; i++) Attach(service);
        Assert.ThrowsExactly<ArgumentException>(() => Attach(service));
        service.Configure(Config); Assert.IsFalse(service.IsBusy(owner)); // Equal configuration preserves ownership.
        Assert.ThrowsExactly<ArgumentException>(() => new ReleaseCheckService(Guid.Empty, false, new Provider()));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ReleaseCheckService(Guid.NewGuid(), false, null!));
        Assert.ThrowsExactly<ArgumentException>(() => new ReleaseCheckConfiguration(new string('a', 40), "novel", "1.0.0"));
        Assert.ThrowsExactly<ArgumentException>(() => new ReleaseCheckConfiguration("writer", new string('a', 101), "1.0.0"));
        Assert.ThrowsExactly<ArgumentException>(() => new ReleaseCheckConfiguration("writer", "novel", "bad"));
    }

    [TestMethod]
    public async Task ProfileContextsAndPreviewAreIsolatedAndHeldResultDoesNotExpire()
    {
        var project = Guid.NewGuid(); var clock = new Clock(); var provider = new Provider();
        using var preview = new ReleaseCheckService(project, true, provider, Config, clock);
        using var game = new ReleaseCheckService(project, false, provider, Config, clock);
        var owner = Attach(preview); preview.Check(owner); preview.Poll();
        provider.Reply.SetResult(new(ReleaseCheckCode.Ok, Data())); await Finish(preview);
        clock.Advance(20); Assert.IsTrue(preview.IsBusy(owner)); Assert.IsTrue(Take(preview, owner).Success);
        var player = Attach(game); game.Check(player); game.Poll(); await Finish(game);
        Assert.IsFalse(Take(game, player).Release!.FromCache); Assert.AreEqual(2, provider.Calls);
    }

    private sealed class DispatchClock : TimeProvider
    {
        private int calls;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var call = Interlocked.Increment(ref calls);
            return TimeSpan.FromSeconds(call >= 5 ? 11 : call >= 3 ? 9 : 0).Ticks;
        }
    }

    [TestMethod]
    public void DeadlineCrossingDuringDispatchIsAResultRatherThanAnException()
    {
        var provider = new Provider();
        using var service = new ReleaseCheckService(Guid.NewGuid(), false, provider, Config, new DispatchClock());
        var owner = Attach(service); service.Check(owner); service.Poll();
        Assert.AreEqual(ReleaseCheckCode.Timeout, Take(service, owner).Code);
        Assert.IsFalse(service.HasRunningTransport); Assert.AreEqual(0, provider.Calls);
    }
}
