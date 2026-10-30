namespace NovellMaker.Core.ExternalServices;

/// <summary>Single-host-thread coordinator. Only Poll publishes background work; no gameplay or disk ownership.</summary>
public sealed class ReleaseCheckService : IDisposable, IAsyncDisposable
{
    public const int MaximumWaiting = 4;
    public const int MaximumReservations = 128;
    public const int MaximumRequestsPerOwner = 128;
    public const int MaximumOwners = 64;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    public sealed class Owner
    {
        internal Owner(Guid instanceId, long generation) { InstanceId = instanceId; Generation = generation; }
        public Guid InstanceId { get; }
        public long Generation { get; }
        internal bool Closed;
        internal readonly Dictionary<Guid, Request> Requests = [];
        internal readonly Queue<Request> Results = [];
        internal Request? Outstanding;
    }

    internal sealed class Request(Owner owner, Guid id, Guid origin, int depth, long accepted, ReleaseCheckConfiguration? configuration)
    {
        internal readonly Owner Owner = owner;
        internal readonly Guid Id = id;
        internal readonly Guid Origin = origin;
        internal int Depth = depth;
        internal readonly long Accepted = accepted;
        internal readonly ReleaseCheckConfiguration? Configuration = configuration;
        internal ReleaseCheckResult? Result;
        internal bool Released;
    }

    private sealed class Work(Request request, CancellationTokenSource cancellation, Task<ReleaseProviderReply> task)
    {
        internal readonly Request Request = request;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly Task<ReleaseProviderReply> Task = task;
        internal Task CancellationSignal = System.Threading.Tasks.Task.CompletedTask;
        internal bool CancellationSignalled;
    }
    private readonly IReleaseCheckProvider provider;
    private readonly TimeProvider clock;
    private readonly HashSet<Owner> owners = [];
    private readonly Queue<Request> waiting = [];
    private Work? active;
    private ReleaseCheckConfiguration? configuration;
    private ReleaseCheckData? cache;
    private long cachedAt;
    private long cooldownStarted;
    private TimeSpan cooldownDuration;
    private int reservations;
    private bool disposed;

    public Guid ProjectId { get; }
    public bool IsPreview { get; }
    public int ReservationCount => reservations;
    public bool HasRunningTransport => active is not null;
    public int WaitingCount => Math.Max(0, waiting.Count - (active is null ? 1 : 0));

    public ReleaseCheckService(Guid projectId, bool isPreview, IReleaseCheckProvider provider,
        ReleaseCheckConfiguration? configuration = null, TimeProvider? clock = null)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Не указан проект.", nameof(projectId));
        ProjectId = projectId; IsPreview = isPreview;
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.configuration = configuration; this.clock = clock ?? TimeProvider.System;
    }

    public Owner Attach(Guid instanceId, long generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (instanceId == Guid.Empty || generation < 0 || owners.Count >= MaximumOwners ||
            owners.Any(o => o.InstanceId == instanceId)) throw new ArgumentException("Некорректный владелец проверки выпуска.");
        var owner = new Owner(instanceId, generation);
        owners.Add(owner); return owner;
    }

    private void ValidateOwner(Owner owner)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owner is null || owner.Closed || !owners.Contains(owner)) throw new ArgumentException("Владелец проверки выпуска недействителен.");
    }

    public bool IsCurrentOwner(Owner owner) => !disposed && owner is not null && !owner.Closed && owners.Contains(owner);

    public bool IsBusy(Owner owner) { ValidateOwner(owner); return owner.Outstanding is not null; }

    /// <summary>Reserves bounded result storage before effects. Call only after the caller's entire batch was validated.</summary>
    public void ValidateCheck(Owner owner, Guid requestId, int sourceDepth = 0, Guid? origin = null)
    {
        ValidateOwner(owner);
        if (requestId == Guid.Empty || owner.Requests.ContainsKey(requestId) || sourceDepth is < 0 or >= 32 || origin == Guid.Empty)
            throw new ArgumentException("Превышен бюджет цепочки или неверный ID проверки выпуска.");
        if (owner.Requests.Count >= MaximumRequestsPerOwner || reservations >= MaximumReservations)
            throw new InvalidOperationException("Превышен бюджет запросов или результатов проверки выпуска.");
    }

    public Guid Check(Owner owner, int sourceDepth = 0, Guid? origin = null, Guid? requestId = null)
    {
        var id = requestId ?? Guid.NewGuid();
        ValidateCheck(owner, id, sourceDepth, origin);
        var request = new Request(owner, id, origin ?? Guid.NewGuid(), sourceDepth + 1, clock.GetTimestamp(), configuration);
        owner.Requests.Add(request.Id, request); reservations++;
        if (configuration is null) Complete(request, ReleaseCheckCode.Disabled);
        else if (owner.Outstanding is not null) Complete(request, ReleaseCheckCode.Busy);
        else if (FreshCache() is { } data) { owner.Outstanding = request; Complete(request, ReleaseCheckCode.Ok, data with { FromCache = true }); }
        else if (CooldownSeconds() is { } delay) { owner.Outstanding = request; Complete(request, ReleaseCheckCode.RateLimited, retry: delay); }
        else if (waiting.Count >= MaximumWaiting + (active is null ? 1 : 0)) Complete(request, ReleaseCheckCode.Busy);
        else { owner.Outstanding = request; waiting.Enqueue(request); }
        return request.Id;
    }

    /// <summary>Preflight for an atomic caller batch. No cancellation or result mutation occurs here.</summary>
    public void ValidateCancel(Owner owner, Guid requestId, int sourceDepth = 0)
    {
        ValidateOwner(owner);
        if (sourceDepth is < 0 or > 32 || !owner.Requests.TryGetValue(requestId, out var request))
            throw new ArgumentException("Некорректная отмена проверки выпуска.");
        if (!request.Released && request.Result is null && Math.Max(request.Depth, sourceDepth + 1) > 32)
            throw new InvalidOperationException("Превышен бюджет цепочки проверки выпуска.");
    }

    public void Cancel(Owner owner, Guid requestId, int sourceDepth = 0)
    {
        ValidateCancel(owner, requestId, sourceDepth);
        var request = owner.Requests[requestId];
        if (request.Released || request.Result is not null) return;
        request.Depth = Math.Max(request.Depth, sourceDepth + 1);
        if (active?.Request == request) SignalCancellation(active);
        Complete(request, ReleaseCheckCode.Cancelled);
        RemoveFinishedWaiting();
    }

    public bool TryTakeResult(Owner owner, out ReleaseCheckResult? result)
    {
        ValidateOwner(owner);
        if (!owner.Results.TryDequeue(out var request)) { result = null; return false; }
        result = request.Result;
        Release(request);
        return true;
    }

    /// <summary>Host bridge retains reservations/busy ownership until the result handler commits.</summary>
    public System.Collections.Immutable.ImmutableArray<ReleaseCheckResult> PendingResults(Owner owner)
    {
        ValidateOwner(owner);
        return [.. owner.Results.Select(r => r.Result!)];
    }

    public void Close(Owner owner)
    {
        if (owner is null || !owners.Remove(owner)) return;
        owner.Closed = true;
        if (active?.Request.Owner == owner) SignalCancellation(active);
        foreach (var request in owner.Requests.Values) Release(request);
        owner.Requests.Clear(); owner.Results.Clear(); owner.Outstanding = null;
        RemoveFinishedWaiting();
    }

    /// <summary>NewGame/MainMenu/successful Load close their origins, not the profile cache or cooldown.</summary>
    public void CloseGeneration(long generation)
    {
        foreach (var owner in owners.Where(o => o.Generation == generation).ToArray()) Close(owner);
    }

    public void Configure(ReleaseCheckConfiguration? value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (configuration == value) return;
        foreach (var owner in owners.ToArray()) Close(owner);
        configuration = value; cache = null; // Deliberately retain the common host cooldown.
    }

    /// <summary>Never awaits. Tasks (including synchronously blocking adapters) execute away from the host.</summary>
    public void Poll()
    {
        if (disposed) return;
        if (active is { } work)
        {
            var request = work.Request;
            if (!request.Released && request.Result is null && Elapsed(request.Accepted) >= Deadline)
            { SignalCancellation(work); Complete(request, ReleaseCheckCode.Timeout); }
            if (work.Task.IsCompleted)
            {
                active = null;
                try
                {
                    var reply = work.Task.GetAwaiter().GetResult();
                    if (!request.Released && request.Result is null)
                    {
                        if (!ValidReply(reply, request.Configuration!)) reply = new(ReleaseCheckCode.InvalidResponse);
                        if (reply.RetryAfterSeconds is { } delay) ExtendCooldown(TimeSpan.FromSeconds(delay));
                        if (reply.Code == ReleaseCheckCode.Ok)
                        { cache = reply.Release! with { FromCache = false }; cachedAt = clock.GetTimestamp(); }
                        Complete(request, reply.Code, reply.Release, reply.Code == ReleaseCheckCode.RateLimited ? Math.Max(60, reply.RetryAfterSeconds ?? 60) : null);
                        if (reply.Code == ReleaseCheckCode.RateLimited) ExtendCooldown(TimeSpan.FromSeconds(Math.Max(60, reply.RetryAfterSeconds ?? 60)));
                    }
                }
                catch (OperationCanceledException)
                { if (!request.Released && request.Result is null) Complete(request, ReleaseCheckCode.Timeout); }
                catch (Exception)
                { if (!request.Released && request.Result is null) Complete(request, ReleaseCheckCode.InvalidResponse); }
                finally { DisposeCancellation(work); }
            }
        }
        while (waiting.TryPeek(out var request))
        {
            if (request.Released || request.Result is not null) { waiting.Dequeue(); continue; }
            if (Elapsed(request.Accepted) >= Deadline) Complete(request, ReleaseCheckCode.Timeout);
            else if (FreshCache() is { } data) Complete(request, ReleaseCheckCode.Ok, data with { FromCache = true });
            else if (CooldownSeconds() is { } delay) Complete(request, ReleaseCheckCode.RateLimited, retry: delay);
            else if (active is not null) break;
            else
            {
                // The deadline can pass between the first check and dispatch. Never pass a negative duration to CTS.
                var remaining = Deadline - Elapsed(request.Accepted);
                if (remaining <= TimeSpan.Zero) Complete(request, ReleaseCheckCode.Timeout);
                else
                {
                    var cancellation = new CancellationTokenSource(remaining, clock);
                    ExtendCooldown(MinimumInterval); // Reserve interval on host before Task.Run can send HTTP.
                    var task = Task.Run(() => provider.CheckAsync(request.Configuration!, cancellation.Token), CancellationToken.None);
                    active = new(request, cancellation, task);
                }
            }
            waiting.Dequeue();
        }
    }

    private static bool ValidReply(ReleaseProviderReply? reply, ReleaseCheckConfiguration config) =>
        reply is not null && Enum.IsDefined(reply.Code) &&
        reply.Code is not (ReleaseCheckCode.Disabled or ReleaseCheckCode.Busy or ReleaseCheckCode.Cancelled) &&
        reply.RetryAfterSeconds is not <= 0 &&
        (reply.Code == ReleaseCheckCode.Ok ? reply.Release is { FromCache: false } data && data.IsValid(config) : reply.Release is null);

    private TimeSpan Elapsed(long start) => clock.GetElapsedTime(start, clock.GetTimestamp());
    private ReleaseCheckData? FreshCache() => cache is not null && Elapsed(cachedAt) < CacheLifetime ? cache : null;
    private int? CooldownSeconds()
    {
        var remaining = cooldownDuration - Elapsed(cooldownStarted);
        return remaining > TimeSpan.Zero ? (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalSeconds)) : null;
    }
    private void ExtendCooldown(TimeSpan duration)
    {
        var remaining = cooldownDuration - Elapsed(cooldownStarted);
        cooldownStarted = clock.GetTimestamp(); cooldownDuration = remaining > duration ? remaining : duration;
    }

    private void Complete(Request request, ReleaseCheckCode code, ReleaseCheckData? data = null, int? retry = null)
    {
        if (request.Released || request.Result is not null) return;
        request.Result = new(request.Id, code, data, retry, request.Origin, request.Depth);
        request.Owner.Results.Enqueue(request);
    }
    private void Release(Request request)
    {
        if (request.Released) return;
        request.Released = true; reservations--;
        if (request.Owner.Outstanding == request) request.Owner.Outstanding = null;
    }
    private void RemoveFinishedWaiting()
    {
        var remaining = waiting.Where(r => !r.Released && r.Result is null).ToArray();
        waiting.Clear(); foreach (var request in remaining) waiting.Enqueue(request);
    }

    private static void SignalCancellation(Work work)
    {
        // Cancellation callbacks belong to the transport and must not block the game thread.
        if (work.CancellationSignalled) return;
        work.CancellationSignalled = true;
        work.CancellationSignal = work.Cancellation.CancelAsync();
        _ = work.CancellationSignal.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void DisposeCancellation(Work work) =>
        _ = work.CancellationSignal.ContinueWith(t => { _ = t.Exception; work.Cancellation.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        if (disposed) return;
        foreach (var owner in owners.ToArray()) Close(owner);
        disposed = true; cache = null;
        if (active is { } work)
        {
            _ = work.Task.ContinueWith(t => { _ = t.Exception; DisposeCancellation(work); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (active is not { } work) return;
        try { await work.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { } // Shutdown is bounded; late completion remains observed by Dispose.
    }
}
