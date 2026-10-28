using System.Collections.Immutable;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Triggers;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Animations;

namespace NovellMaker.Core.Screens.Lua;

public enum ScreenEventAdmission { Accepted, Coalesced, Busy, Unavailable }
public enum ScreenLuaIssueKind { StaleEvent, QueueExpired, Busy, Handler, Conversation }
public sealed record ScreenLuaIssue(ScreenLuaIssueKind Kind, Guid? InstanceId, string Message);

/// <summary>Single-host-thread lifecycle/queue. Only Poll commits asynchronously prepared batches.</summary>
public sealed partial class ScreenLuaSession : IDisposable, IAsyncDisposable
{
    public const int MaximumQueuedEvents = 128;
    private sealed class QueueExpiredException() : IOException("Превышен срок ожидания события экрана (5 секунд): событие не исполнено.");
    private sealed class Entry(ScreenInstance instance, string source, ImmutableDictionary<string, Guid> references,
        ImmutableDictionary<string, string> snapshot)
    {
        internal readonly ScreenInstance Instance = instance;
        internal readonly string Source = source;
        internal readonly ImmutableDictionary<string, Guid> References = references;
        internal readonly ImmutableDictionary<string, string> Snapshot = snapshot;
        internal long RemoteVersion;
        internal bool Loaded;
        internal bool Closing;
        internal string? Error;
        internal LuaValue State = LuaValue.Null;
        internal LuaRandomState RandomState = LuaRandomState.Initial;
        internal LuaModuleLibrary? Modules;
        internal NovellMaker.Core.Minigames.MinigameDefinition? Minigame;
        internal MinigameInvocation? Invocation;
        internal NovellMaker.Core.Minigames.MinigameResult? MinigameResult;
        internal long? SuspendedAt;
        internal ImmutableHashSet<Guid> DiagnosticScripts = [];
        internal ScreenAnimator Animator = new();
        internal bool AnimationDirty;
        internal NovellMaker.Core.Audio.AudioOwner AudioOwner = null!;
        internal ImmutableDictionary<string, Guid> AudioReferences = ImmutableDictionary<string, Guid>.Empty;
        internal readonly Dictionary<Guid, AudioTicket> Audio = [];
    }
    private sealed record Work(Entry Entry, string Kind, ScreenLuaEvent? Event,
        ImmutableDictionary<string, string> Snapshot, long Submitted, int ChainDepth = 0);
    private sealed record ServiceReply(Entry Entry, GameServiceAction Action, int ChainDepth);
    private sealed class Conversation(IScreenLuaConnection connection)
    {
        internal readonly IScreenLuaConnection Connection = connection;
        internal readonly CancellationTokenSource Cancellation = new();
        internal bool Ready;
    }
    private sealed record Prepared(ScreenLuaResponse Response, ScreenPreparedBatch? Batch, ImmutableHashSet<Guid>? SourceIds = null);
    private sealed record Pending(Work Work, ScreenLuaStamp Stamp, Task<Prepared> Task);
    private readonly ScreenStack stack;
    private readonly Func<IScreenLuaConnection> factory;
    private readonly TimeProvider clock;
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly LinkedList<Work> queue = [];
    private readonly Queue<Entry> closes = [];
    private readonly List<Task> retiring = [];
    private readonly Queue<string> diagnostics = [];
    private readonly Queue<ScreenLuaIssue> issues = [];
    private readonly Queue<ScreenServiceInvocation> services = [];
    private readonly Dictionary<Guid, ServiceReply> serviceReplies = [];
    private Conversation conversation;
    private Pending? pending;
    private readonly Dictionary<Guid, Pending> held = [];
    private bool disposed;
    private bool faulted;

    public ScreenLuaSession(ScreenStack stack) : this(stack, () => new ScreenLuaConnection()) { }
    internal ScreenLuaSession(ScreenStack stack, Func<IScreenLuaConnection> factory, TimeProvider? clock = null)
    {
        this.stack = stack ?? throw new ArgumentNullException(nameof(stack));
        this.factory = factory;
        this.clock = clock ?? TimeProvider.System;
        conversation = new(factory());
    }

    public long Generation { get; private set; } = 1;
    public int AppliedBatches { get; private set; }
    public int QueuedEvents => queue.Count;
    public int ReservedServiceReplies => serviceReplies.Count;
    private int Occupancy => queue.Count + serviceReplies.Count + entries.Values.Sum(e => e.Animator.Reserved + e.Audio.Values.Count(t => !t.Queued));
    public bool HasAnimations(ScreenInstance instance) => IsAvailable(instance) &&
        (entries[instance.Id].Animator.Busy || queue.Any(w => ReferenceEquals(w.Entry.Instance, instance) && w.Event?.Animation is not null) ||
         pending is { } p && ReferenceEquals(p.Work.Entry.Instance, instance) && p.Work.Event?.Animation is not null ||
         held.TryGetValue(instance.Id, out var h) && h.Work.Event?.Animation is not null);
    public int ActiveAnimations => entries.Values.Where(e => IsAvailable(e.Instance)).Sum(e => e.Animator.ActiveCount);

    /// <summary>Virtual active time, called once per host frame. No RPC or user callbacks per frame.</summary>
    public bool AdvanceAnimations(TimeSpan elapsed, Func<ScreenInstance, bool> active,
        ImmutableDictionary<string, string>? snapshot = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        Reconcile(); var changed = false;
        foreach (var entry in entries.Values)
        {
            if (!IsAvailable(entry.Instance) || entry.SuspendedAt is not null || !active(entry.Instance) || !IsSettled(entry.Instance)) continue;
            var before = entry.Instance.Definition;
            var after = entry.Animator.Tick(before, seconds);
            if (!ReferenceEquals(before, after)) { stack.Commit(entry.Instance, entry.Instance.Version, after, animationFrame: true); entry.AnimationDirty = true; changed = true; }
            DrainAnimations(entry, snapshot ?? entry.Snapshot);
        }
        return changed;
    }
    private void DrainAnimations(Entry entry, ImmutableDictionary<string, string> snapshot)
    {
        foreach (var result in entry.Animator.Drain())
            queue.AddLast(new Work(entry, "event", new("animation_result") { Animation = result }, snapshot, clock.GetTimestamp(), result.ChainDepth));
    }
    public bool HasPending => pending is not null || queue.Count != 0 || closes.Count != 0 || held.Count != 0;
    public bool HasRunnablePending => pending is { Work.Entry.SuspendedAt: null } || closes.Count != 0 ||
        queue.Any(w => w.Entry.SuspendedAt is null) || held.Values.Any(p => p.Work.Entry.SuspendedAt is null || p.Work.Entry.Closing);
    public Task? PendingTask => pending?.Task;
    public ImmutableArray<string> Diagnostics => [.. diagnostics];
    public ImmutableArray<ScreenLuaIssue> Issues => [.. issues];
    public LuaDiagnosticJournal Journal { get; } = new();
    public bool IsAvailable(ScreenInstance instance) => !disposed && entries.TryGetValue(instance.Id, out var entry) &&
        ReferenceEquals(entry.Instance, instance) && entry.Error is null && !entry.Closing && Current(instance);

    public bool IsSettled(ScreenInstance instance) => IsAvailable(instance) && entries[instance.Id].Loaded &&
        pending?.Work.Entry.Instance.Id != instance.Id && !held.ContainsKey(instance.Id) && !queue.Any(w => w.Entry.Instance.Id == instance.Id);
    public NovellMaker.Core.Minigames.MinigameResult? GetMinigameResult(ScreenInstance instance) =>
        IsAvailable(instance) ? entries[instance.Id].MinigameResult : null;
    public void Suspend(ScreenInstance instance, bool suspended)
    {
        if (!IsAvailable(instance)) return;
        var entry = entries[instance.Id];
        if (suspended) entry.SuspendedAt ??= clock.GetTimestamp();
        else if (entry.SuspendedAt is { } start)
        {
            var duration = clock.GetTimestamp() - start; entry.SuspendedAt = null;
            for (var item = queue.First; item is not null; item = item.Next)
                if (ReferenceEquals(item.Value.Entry, entry)) item.Value = item.Value with { Submitted = item.Value.Submitted + duration };
        }
    }

    /// <summary>Last host-committed explicit data, including after a handler failure. Never the pending VM state.</summary>
    public LuaValue GetState(ScreenInstance instance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!entries.TryGetValue(instance.Id, out var entry) || !ReferenceEquals(entry.Instance, instance) || !Current(instance))
            throw new ArgumentException("Чужой или закрытый экземпляр состояния Lua.");
        return entry.State;
    }

    /// <summary>Last host-committed random stream; safe to serialize and pass to a new Attach.</summary>
    public LuaRandomState GetRandomState(ScreenInstance instance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!entries.TryGetValue(instance.Id, out var entry) || !ReferenceEquals(entry.Instance, instance) || !Current(instance))
            throw new ArgumentException("Чужой или закрытый экземпляр RNG.");
        return entry.RandomState;
    }

    public ImmutableArray<ScreenServiceInvocation> DrainServices()
    {
        var result = services.Where(s => IsAvailable(s.Instance)).ToImmutableArray(); services.Clear(); return result;
    }

    /// <summary>External project binding: reserve a bounded reply before starting its host service.</summary>
    public ScreenEventAdmission ReserveServiceReply(ScreenInstance instance, GameServiceCommand command)
    {
        command.Validate();
        Reconcile();
        if (!IsAvailable(instance)) return ScreenEventAdmission.Unavailable;
        if (Occupancy >= MaximumQueuedEvents || serviceReplies.ContainsKey(command.RequestId))
            return ScreenEventAdmission.Busy;
        serviceReplies.Add(command.RequestId, new(entries[instance.Id], command.Action, 1));
        return ScreenEventAdmission.Accepted;
    }

    public void Attach(ScreenInstance instance, string source, ImmutableDictionary<string, Guid>? references = null,
        ImmutableDictionary<string, string>? snapshot = null, LuaValue? state = null, LuaModuleLibrary? modules = null, LuaRandomState? randomState = null,
        NovellMaker.Core.Minigames.MinigameDefinition? minigame = null, MinigameInvocation? invocation = null,
        ImmutableDictionary<string, Guid>? audioReferences = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LuaLimits.CheckSource(source);
        references ??= ImmutableDictionary<string, Guid>.Empty;
        audioReferences ??= ImmutableDictionary<string, Guid>.Empty;
        if (!LuaActionScript.ValidReferences(audioReferences)) throw new ArgumentException("Неверные ссылки звука Lua.");
        snapshot ??= ImmutableDictionary<string, string>.Empty;
        if (!Current(instance) || instance.Version != 0 || entries.ContainsKey(instance.Id) ||
            !LuaActionScript.ValidReferences(references) || references.Any(r => !instance.Definition.Nodes.Any(n => n.Id == r.Value)) ||
            !LuaLimits.ValidVariables(snapshot)) throw new ArgumentException("Неверный владелец, ссылки или снимок экранного Lua.");
        if (faulted) Reset();
        if ((minigame is null) != (invocation is null)) throw new ArgumentException("Нет контекста мини-игры.");
        if (minigame is not null)
        { minigame.Validate(); minigame.ParametersSchema.Validate(invocation!.Parameters); minigame.StateSchema.Validate(state!); }
        if (Occupancy >= MaximumQueuedEvents) throw new InvalidOperationException("Очередь событий экрана заполнена.");
        var entry = new Entry(instance, source, references, snapshot) { State = state ?? LuaValue.Null, Modules = modules, RandomState = randomState ?? LuaRandomState.Initial,
            Minigame = minigame, Invocation = invocation, AudioReferences = audioReferences,
            AudioOwner = new(stack.SessionId, Generation, instance.OwnerId, instance.Id) };
        entries.Add(instance.Id, entry);
        queue.AddLast(new Work(entry, "load", null, snapshot, clock.GetTimestamp()));
    }

    /// <summary>Explicitly reuse the immutable snapshot captured at Attach.</summary>
    public ScreenEventAdmission EnqueueUsingInstanceSnapshot(ScreenInstance instance, ScreenLuaEvent value)
        => IsAvailable(instance) ? Enqueue(instance, value, entries[instance.Id].Snapshot) : ScreenEventAdmission.Unavailable;

    /// <summary>The host captures data at admission; later mutations cannot change this event's snapshot.</summary>
    public ScreenEventAdmission Enqueue(ScreenInstance instance, ScreenLuaEvent value,
        ImmutableDictionary<string, string> snapshot)
    {
        if (!IsAvailable(instance)) return ScreenEventAdmission.Unavailable;
        var entry = entries[instance.Id];
        ScreenLuaProtocol.ValidateEvent(stack.VisualDefinition(instance), value);
        if (value.Audio is not null) return ScreenEventAdmission.Unavailable; // Host-owned reserved events only.
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!LuaLimits.ValidVariables(snapshot)) throw new ArgumentException("Превышен бюджет снимка события.");
        if (value.Result is { } result)
        {
            if (!serviceReplies.TryGetValue(result.RequestId, out var reply) || !ReferenceEquals(reply.Entry, entry) || reply.Action != result.Action)
                return ScreenEventAdmission.Unavailable;
            serviceReplies.Remove(result.RequestId);
            var response = new Work(entry, "event", value, snapshot, clock.GetTimestamp(), reply.ChainDepth);
            // Replace the reserved slot. Completed replies precede ordinary input, in reply-admission order.
            var ordinary = queue.First;
            // A project binding may finish before its handler has loaded. Never send its reply to an absent VM.
            var load = queue.Last;
            while (load is not null && load.Value.Kind != "load") load = load.Previous;
            if (load is not null) ordinary = load.Next;
            while (ordinary is not null && ordinary.Value.Event?.Kind == "service_result") ordinary = ordinary.Next;
            if (ordinary is null) queue.AddLast(response); else queue.AddBefore(ordinary, response);
            return ScreenEventAdmission.Accepted;
        }
        var work = new Work(entry, "event", value, snapshot, clock.GetTimestamp());
        // Coalesce only adjacent motion; never move it across a significant click.
        if (value.Kind is "move" or "hover" && queue.Last is { } last &&
            ReferenceEquals(last.Value.Entry, entry) && last.Value.Event?.Kind == value.Kind && last.Value.Event.NodeId == value.NodeId)
        { last.Value = work; return ScreenEventAdmission.Coalesced; }
        if (Occupancy >= MaximumQueuedEvents)
        { Report("Очередь событий экрана заполнена: событие не принято.", ScreenLuaIssueKind.Busy, instance.Id); return ScreenEventAdmission.Busy; }
        queue.AddLast(work);
        return ScreenEventAdmission.Accepted;
    }

    public bool Poll()
    {
        if (disposed) return false;
        Reconcile();
        // Hold completed data, not the conversation. A menu above a paused mini-game can keep using Lua.
        if (pending is { Task.IsCompleted: true, Work.Entry.SuspendedAt: not null } suspended && !suspended.Work.Entry.Closing)
        { held.Add(suspended.Work.Entry.Instance.Id, suspended); pending = null; }
        if (pending is null && held.Values.FirstOrDefault(p => p.Work.Entry.SuspendedAt is null || p.Work.Entry.Closing) is { } resumed)
        { held.Remove(resumed.Work.Entry.Instance.Id); pending = resumed; }
        var changed = false;
        if (pending is { } ready && ready.Task.IsCompleted && (ready.Work.Entry.SuspendedAt is null || ready.Work.Entry.Closing))
        {
            pending = null;
            try
            {
                var result = ready.Task.GetAwaiter().GetResult(); // IsCompleted checked: never waits in a frame.
                var entry = ready.Work.Entry;
                if (ready.Work.Kind != "close" && Current(entry.Instance) && !entry.Closing && entry.Instance.Version == ready.Stamp.Version)
                {
                    if (result.SourceIds is { } ids) entry.DiagnosticScripts = ids;
                    PublishLogs(ready.Work, result.Response);
                }
                if (ready.Work.Kind == "close")
                {
                    if (result.Response.Error is { } closeError) throw new IOException(closeError);
                    entry.Loaded = false;
                }
                else if (result.Response.Error is { } error)
                {
                    entry.Loaded = false;
                    entry.Error = error;
                    if (Current(entry.Instance) && !entry.Closing) Report(error, ScreenLuaIssueKind.Handler, entry.Instance.Id);
                    RemoveQueued(entry);
                }
                else
                {
                    entry.Loaded = true; entry.RemoteVersion = ready.Stamp.Version + 1;
                    if (Current(entry.Instance) && !entry.Closing && entry.Instance.Version == ready.Stamp.Version)
                    {
                        try
                        {
                            if (result.Batch!.Service is not null && services.Count >= MaximumQueuedEvents)
                                throw new InvalidOperationException("Очередь сервисов экрана заполнена.");
                            if (result.Batch.Service is { } request && (serviceReplies.ContainsKey(request.RequestId) ||
                                Occupancy >= MaximumQueuedEvents))
                                throw new InvalidOperationException("Нельзя зарезервировать ответ сервиса экрана.");
                            if ((!result.Batch.Triggers.IsEmpty || result.Batch.Service is not null) && ready.Work.ChainDepth >= AuthorTrigger.MaximumChainDepth)
                                throw new InvalidOperationException("Превышен бюджет глубины цепочки триггеров и сервисов.");
                            if (!result.Batch.Triggers.IsEmpty && Occupancy + result.Batch.Triggers.Length > MaximumQueuedEvents)
                                throw new InvalidOperationException("Превышен бюджет очереди или глубины цепочки триггеров.");
                            var animator = entry.Animator.Prepare(entry.Instance.Definition, result.Batch.AnimationChanges, ready.Work.ChainDepth);
                            var audioReservations = ValidateAudio(entry, result.Batch.AnimationChanges, ready.Work.ChainDepth);
                            if (Occupancy - entry.Animator.Reserved + animator.Reserved + audioReservations + result.Batch.Triggers.Length + (result.Batch.Service is null ? 0 : 1) > MaximumQueuedEvents)
                                throw new InvalidOperationException("Нет места для событий завершения анимаций.");
                            stack.Commit(entry.Instance, ready.Stamp.Version, result.Batch!.Definition);
                            entry.Animator = animator;
                            entry.AnimationDirty = false;
                            if (result.Batch.State is { } state) entry.State = state;
                            if (result.Batch.RandomState is { } random) entry.RandomState = random;
                            if (result.Batch.MinigameResult is { } completion) entry.MinigameResult = completion;
                            AppliedBatches++;
                            CommitAudio(entry, result.Batch.AnimationChanges, ready.Work.ChainDepth);
                            if (ready.Work.Event?.Audio is { } audioResult) entry.Audio.Remove(audioResult.Id);
                            DrainAnimations(entry, ready.Work.Snapshot);
                            foreach (var trigger in result.Batch.Triggers)
                                queue.AddLast(new Work(entry, "event", new("trigger") { Trigger = trigger }, ready.Work.Snapshot,
                                    clock.GetTimestamp(), ready.Work.ChainDepth + 1));
                            if (result.Batch.Service is { } service)
                            {
                                serviceReplies.Add(service.RequestId, new(entry, service.Action, ready.Work.ChainDepth + 1));
                                services.Enqueue(new(entry.Instance, service));
                            }
                            if (result.Batch.Close) stack.Close(entry.Instance);
                            changed = true;
                        }
                        catch (InvalidOperationException commitError)
                        {
                            entry.Error = commitError.Message; RemoveQueued(entry); ScheduleClose(entry);
                            Report(commitError.Message, ScreenLuaIssueKind.Handler, entry.Instance.Id);
                            PublishLogs(ready.Work, result.Response with { Logs = [], DroppedLogs = 0, Fault = new("error", LuaLogBuffer.Clip(commitError.Message)) });
                        }
                    }
                    else Detach(entry);
                    if (entry.Closing) ScheduleClose(entry);
                }
            }
            catch (QueueExpiredException error) { Expire(ready.Work, error.Message); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
            { Fail(error.Message, ready.Work); }
            Reconcile();
        }
        retiring.RemoveAll(t => t.IsCompleted);
        if (pending is null && !faulted && retiring.Count == 0) StartNext();
        return changed;
    }

    private void StartNext()
    {
        Work? work = null;
        if (closes.TryDequeue(out var closing))
            work = new(closing, "close", null, ImmutableDictionary<string, string>.Empty, clock.GetTimestamp());
        else
        {
            var first = queue.First;
            while (first is not null && first.Value.Entry.SuspendedAt is not null) first = first.Next;
            if (first is not null) { work = first.Value; queue.Remove(first); }
        }
        if (work is null) return;
        var entry = work.Entry;
        if (clock.GetElapsedTime(work.Submitted) >= TimeSpan.FromSeconds(5))
        {
            Expire(work, "Превышен срок ожидания события экрана (5 секунд): событие не исполнено.");
            return;
        }
        if (work.Event is { Kind: "click" } click && !stack.VisualDefinition(entry.Instance).Buttons.Any(n => n.Id == click.NodeId))
        {
            Report("Устаревшее нажатие экрана пропущено.", ScreenLuaIssueKind.StaleEvent, entry.Instance.Id);
            return;
        }
        var stamp = new ScreenLuaStamp(Guid.NewGuid(), stack.SessionId, Generation, entry.Instance.OwnerId,
            entry.Instance.Id, entry.Instance.Version);
        if (work.Kind == "close") stamp = stamp with { Version = entry.RemoteVersion };
        var context = conversation;
        var captured = work;
        var definition = entry.Instance.Definition;
        var state = entry.State;
        var random = entry.RandomState;
        var eventDefinition = work.Event is null ? definition : stack.VisualDefinition(entry.Instance);
        var synchronizeAnimation = entry.AnimationDirty;
        var task = Task.Run(() => PrepareAsync(context, captured, stamp, definition, eventDefinition, state, random, clock, synchronizeAnimation));
        ScreenLuaConnection.Observe(task);
        pending = new(work, stamp, task);
    }

    private static async Task<Prepared> PrepareAsync(Conversation conversation, Work work, ScreenLuaStamp stamp,
        ScreenDefinition definition, ScreenDefinition eventDefinition, LuaValue state, LuaRandomState random, TimeProvider clock, bool synchronizeAnimation)
    {
        var remaining = TimeSpan.FromSeconds(5) - clock.GetElapsedTime(work.Submitted);
        // Task scheduling is part of queue time. No exchange has touched the conversation yet.
        conversation.Cancellation.Token.ThrowIfCancellationRequested();
        if (remaining <= TimeSpan.Zero) throw new QueueExpiredException();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(conversation.Cancellation.Token, deadline.Token);
        var token = operation.Token;
        var exchangeStarted = false;
        void CheckDeadline()
        {
            if (!exchangeStarted && !conversation.Cancellation.IsCancellationRequested &&
                (deadline.IsCancellationRequested || clock.GetElapsedTime(work.Submitted) >= TimeSpan.FromSeconds(5)))
                throw new QueueExpiredException();
            token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(work.Submitted) >= TimeSpan.FromSeconds(5)) throw new IOException("Превышен общий срок события экрана (5 секунд).");
        }
        CheckDeadline();
        var entry = work.Entry;
        LuaModuleBundle? modules = null;
        if (work.Kind == "load" && entry.Modules is not null)
        {
            try { modules = entry.Modules.Prepare(entry.Source, CheckDeadline); }
            catch (ArgumentException error)
            {
                CheckDeadline();
                var message = error.Message.Length > 4096 ? error.Message[..4096] : error.Message;
                return new(new(ScreenLuaProtocol.Version, "error", stamp, message, []), null);
            }
        }
        if (!conversation.Ready)
        {
            var hello = new ScreenLuaRequest(ScreenLuaProtocol.Version, "handshake",
                stamp with { RequestId = Guid.NewGuid(), OwnerId = Guid.Empty, InstanceId = Guid.Empty, Version = 0 },
                Guid.Empty, [], null, ImmutableDictionary<string, Guid>.Empty, null, ImmutableDictionary<string, string>.Empty);
            exchangeStarted = true;
            var helloTask = conversation.Connection.ExchangeAsync(hello, token);
            ScreenLuaConnection.Observe(helloTask);
            var response = await helloTask.WaitAsync(token).ConfigureAwait(false);
            CheckDeadline();
            ScreenLuaConnection.Validate(hello, response);
            if (response.Error is not null) throw new IOException(response.Error);
            conversation.Ready = true;
        }
        if (work.Event is not null) ScreenLuaProtocol.ValidateEvent(eventDefinition, work.Event);
        var request = new ScreenLuaRequest(ScreenLuaProtocol.Version, work.Kind, stamp, definition.Id,
            work.Kind == "load" || work.Kind == "event" && synchronizeAnimation ? definition.Nodes : [], work.Kind == "load" ? entry.Source : null,
            work.Kind == "load" ? entry.References : ImmutableDictionary<string, Guid>.Empty, work.Event, work.Snapshot)
        { State = work.Kind == "close" ? null : state, Modules = modules, RandomState = work.Kind == "close" ? null : random,
            Minigame = work.Kind == "load" ? entry.Invocation : null,
            AudioReferences = work.Kind == "load" ? entry.AudioReferences : null };
        request = await ScreenLuaProtocol.FitServiceResultAsync(request, token).ConfigureAwait(false);
        CheckDeadline();
        exchangeStarted = true;
        var exchange = conversation.Connection.ExchangeAsync(request, token);
        ScreenLuaConnection.Observe(exchange);
        var result = await exchange.WaitAsync(token).ConfigureAwait(false);
        CheckDeadline();
        ScreenLuaConnection.Validate(request, result);
        var sourceIds = modules is null ? entry.DiagnosticScripts : modules.Sources.Select(s => s.Id).Append(modules.RootId).ToImmutableHashSet();
        foreach (var log in result.Logs.Concat(result.Fault is { } fault ? [fault] : []))
            if (log.ScriptId is { } id && !sourceIds.Contains(id)) throw new IOException("Журнал ссылается на чужой Lua-ресурс.");
        var batch = result.Error is null && work.Kind != "close" ? ScreenLuaProtocol.Prepare(definition, result.Changes) : null;
        if (batch is not null)
        {
            if (batch.MinigameResult is { } completion)
            {
                if (entry.Minigame is null) throw new InvalidOperationException("Завершение вне мини-игры.");
                completion.Validate(entry.Minigame);
            }
            if (entry.Minigame is { } minigame)
            {
                if (batch.State is { } data) minigame.StateSchema.Validate(data);
                if (work.Kind == "load" && entry.Invocation!.Restoring &&
                    (batch.State is { } restored && restored.ToJson() != state.ToJson() || batch.RandomState is { } rng && rng != random || batch.MinigameResult is not null || batch.Close || batch.Service is not null || !batch.Triggers.IsEmpty || batch.AnimationChanges.Any(c => c.Kind is "animate" or "cancel_animation" || c.Audio is not null)))
                    throw new InvalidOperationException("Восстановление мини-игры должно только построить представление из снимка.");
            }
        }
        CheckDeadline();
        return new(result, batch, sourceIds);
    }

    private void PublishLogs(Work work, ScreenLuaResponse response)
    {
        var instance = work.Entry.Instance;
        var eventName = work.Kind == "load" ? "load/on_load" : work.Event!.Kind;
        void Add(LuaLogMessage message) => Journal.Add(stack.SessionId, Generation, instance.OwnerId, instance.Id,
            instance.Definition.Id, eventName, message with { ScriptId = message.ScriptId ?? work.Entry.Modules?.RootId });
        foreach (var message in response.Logs) Add(message);
        if (response.DroppedLogs > 0) Add(new("warning", $"Лимит журнала Lua: пропущено записей {response.DroppedLogs}."));
        if (response.Fault is { } fault) Add(fault);
        else if (response.Error is { } error) Add(new("error", LuaLogBuffer.Clip(error)));
    }

    private bool Current(ScreenInstance instance) => stack.Instances.Any(i => ReferenceEquals(i, instance));
    private void Expire(Work work, string message)
    {
        // A control-close cannot be dropped: otherwise the remote VM would be retained indefinitely.
        if (work.Kind == "close") { Fail(message); return; }
        var entry = work.Entry;
        if (Current(entry.Instance) && !entry.Closing)
            Report(message, ScreenLuaIssueKind.QueueExpired, entry.Instance.Id);
        // Nothing was sent: the remote VM and its version are still valid.
        if (work.Kind == "load" || work.Event?.Kind is "service_result" or "animation_result" or "audio_result")
        {
            entry.Error = work.Kind == "load" ? "Истёк срок загрузки экрана." : "Истёк срок обработки ответа сервиса или анимации.";
            RemoveQueued(entry); ScheduleClose(entry);
        }
        if (entry.Closing) ScheduleClose(entry);
    }
    private void RemoveQueued(Entry entry)
    {
        ClearAudio(entry);
        entry.Animator = new();
        var node = queue.First;
        while (node is not null) { var next = node.Next; if (ReferenceEquals(node.Value.Entry, entry)) queue.Remove(node); node = next; }
        foreach (var id in serviceReplies.Where(p => ReferenceEquals(p.Value.Entry, entry)).Select(p => p.Key).ToArray()) serviceReplies.Remove(id);
    }
    private void ScheduleClose(Entry entry)
    {
        if (!entry.Loaded) return;
        entry.Loaded = false;
        closes.Enqueue(entry); // At most one control-close per detached instance, independent of event capacity.
    }
    private void Detach(Entry entry)
    {
        entry.Closing = true;
        entries.Remove(entry.Instance.Id);
        RemoveQueued(entry);
        if (pending?.Work.Entry != entry && !held.ContainsKey(entry.Instance.Id)) ScheduleClose(entry);
    }
    private void Reconcile()
    {
        foreach (var entry in entries.Values.ToArray()) if (!Current(entry.Instance)) Detach(entry);
    }
    private void Report(string message, ScreenLuaIssueKind kind = ScreenLuaIssueKind.Conversation, Guid? instanceId = null, Work? context = null)
    {
        if (diagnostics.Count == 32) diagnostics.Dequeue();
        if (issues.Count == 32) issues.Dequeue();
        var text = message.Length > 4096 ? message[..4096] : message;
        diagnostics.Enqueue(text);
        issues.Enqueue(new(kind, instanceId, text));
        if (kind != ScreenLuaIssueKind.Handler)
        {
            var entry = context?.Entry ?? (instanceId is { } id ? entries.GetValueOrDefault(id) : pending?.Work.Entry);
            Journal.Add(stack.SessionId, Generation, entry?.Instance.OwnerId ?? Guid.Empty, instanceId ?? entry?.Instance.Id ?? Guid.Empty,
                entry?.Instance.Definition.Id ?? Guid.Empty, context?.Event?.Kind ?? kind.ToString(), new("error", LuaLogBuffer.Clip(message), entry?.Modules?.RootId));
        }
    }
    private void Fail(string message, Work? context = null)
    {
        services.Clear();
        serviceReplies.Clear();
        faulted = true;
        foreach (var entry in entries.Values) { entry.Error = message; entry.Loaded = false; entry.Animator = new(); ClearAudio(entry); }
        queue.Clear(); closes.Clear();
        Report(message, context: context);
        Retire();
    }
    private void Retire()
    {
        retiring.RemoveAll(task => task.IsCompleted);
        conversation.Cancellation.Cancel();
        foreach (var item in held.Values) { retiring.Add(item.Task); ScreenLuaConnection.Observe(item.Task); }
        held.Clear();
        if (pending is { } old) { retiring.Add(old.Task); ScreenLuaConnection.Observe(old.Task); pending = null; }
        var previous = conversation;
        var cleanup = Task.Run(async () =>
        {
            var dispose = previous.Connection.DisposeAsync().AsTask();
            ScreenLuaConnection.Observe(dispose);
            await dispose.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        });
        ScreenLuaConnection.Observe(cleanup);
        retiring.Add(cleanup);
    }

    /// <summary>Call only after a successful load/new game; failed loads preserve the conversation.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Retire();
        foreach (var entry in entries.Values) ClearAudio(entry);
        entries.Clear(); queue.Clear(); closes.Clear(); diagnostics.Clear(); issues.Clear();
        services.Clear();
        serviceReplies.Clear();
        Generation++; faulted = false;
        conversation = new(factory());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Retire();
        foreach (var entry in entries.Values) ClearAudio(entry);
        if (audio is not null) audio.PlaybackEnded -= AudioEnded;
        entries.Clear(); queue.Clear(); closes.Clear();
        services.Clear();
        serviceReplies.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        var all = Task.WhenAll(retiring);
        ScreenLuaConnection.Observe(all);
        try { await all.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (OperationCanceledException) { } // Requested shutdown is not a cleanup failure.
        catch (Exception error) { Report("Очистка экранного Lua: " + error.Message); }
    }
}
