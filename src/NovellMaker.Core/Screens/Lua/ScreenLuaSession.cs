using System.Collections.Immutable;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Screens.Lua;

public enum ScreenEventAdmission { Accepted, Coalesced, Busy, Unavailable }
public enum ScreenLuaIssueKind { StaleEvent, QueueExpired, Busy, Handler, Conversation }
public sealed record ScreenLuaIssue(ScreenLuaIssueKind Kind, Guid? InstanceId, string Message);

/// <summary>Single-host-thread lifecycle/queue. Only Poll commits asynchronously prepared batches.</summary>
public sealed class ScreenLuaSession : IDisposable, IAsyncDisposable
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
    }
    private sealed record Work(Entry Entry, string Kind, ScreenLuaEvent? Event,
        ImmutableDictionary<string, string> Snapshot, long Submitted);
    private sealed class Conversation(IScreenLuaConnection connection)
    {
        internal readonly IScreenLuaConnection Connection = connection;
        internal readonly CancellationTokenSource Cancellation = new();
        internal bool Ready;
    }
    private sealed record Prepared(ScreenLuaResponse Response, ScreenPreparedBatch? Batch);
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
    private Conversation conversation;
    private Pending? pending;
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
    public bool HasPending => pending is not null || queue.Count != 0 || closes.Count != 0;
    public Task? PendingTask => pending?.Task;
    public ImmutableArray<string> Diagnostics => [.. diagnostics];
    public ImmutableArray<ScreenLuaIssue> Issues => [.. issues];
    public bool IsAvailable(ScreenInstance instance) => !disposed && entries.TryGetValue(instance.Id, out var entry) &&
        ReferenceEquals(entry.Instance, instance) && entry.Error is null && !entry.Closing && Current(instance);

    public void Attach(ScreenInstance instance, string source, ImmutableDictionary<string, Guid>? references = null,
        ImmutableDictionary<string, string>? snapshot = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LuaLimits.CheckSource(source);
        references ??= ImmutableDictionary<string, Guid>.Empty;
        snapshot ??= ImmutableDictionary<string, string>.Empty;
        if (!Current(instance) || instance.Version != 0 || entries.ContainsKey(instance.Id) ||
            !LuaActionScript.ValidReferences(references) || references.Any(r => !instance.Definition.Nodes.Any(n => n.Id == r.Value)) ||
            !LuaLimits.ValidVariables(snapshot)) throw new ArgumentException("Неверный владелец, ссылки или снимок экранного Lua.");
        if (faulted) Reset();
        if (queue.Count >= MaximumQueuedEvents) throw new InvalidOperationException("Очередь событий экрана заполнена.");
        var entry = new Entry(instance, source, references, snapshot);
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
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!LuaLimits.ValidVariables(snapshot)) throw new ArgumentException("Превышен бюджет снимка события.");
        var work = new Work(entry, "event", value, snapshot, clock.GetTimestamp());
        // Coalesce only adjacent motion; never move it across a significant click.
        if (value.Kind is "move" or "hover" && queue.Last is { } last &&
            ReferenceEquals(last.Value.Entry, entry) && last.Value.Event?.Kind == value.Kind && last.Value.Event.NodeId == value.NodeId)
        { last.Value = work; return ScreenEventAdmission.Coalesced; }
        if (queue.Count >= MaximumQueuedEvents)
        { Report("Очередь событий экрана заполнена: событие не принято.", ScreenLuaIssueKind.Busy, instance.Id); return ScreenEventAdmission.Busy; }
        queue.AddLast(work);
        return ScreenEventAdmission.Accepted;
    }

    public bool Poll()
    {
        if (disposed) return false;
        Reconcile();
        var changed = false;
        if (pending is { } ready && ready.Task.IsCompleted)
        {
            pending = null;
            try
            {
                var result = ready.Task.GetAwaiter().GetResult(); // IsCompleted checked: never waits in a frame.
                var entry = ready.Work.Entry;
                if (ready.Work.Kind == "close")
                {
                    if (result.Response.Error is { } closeError) throw new IOException(closeError);
                    entry.Loaded = false;
                }
                else if (result.Response.Error is { } error)
                {
                    entry.Loaded = false;
                    entry.Error = error;
                    if (Current(entry.Instance)) Report(error, ScreenLuaIssueKind.Handler, entry.Instance.Id);
                    RemoveQueued(entry);
                }
                else
                {
                    entry.Loaded = true; entry.RemoteVersion = ready.Stamp.Version + 1;
                    if (Current(entry.Instance) && !entry.Closing && entry.Instance.Version == ready.Stamp.Version)
                    {
                        try
                        {
                            stack.Commit(entry.Instance, ready.Stamp.Version, result.Batch!.Definition);
                            AppliedBatches++;
                            if (result.Batch.Close) stack.Close(entry.Instance);
                            changed = true;
                        }
                        catch (InvalidOperationException commitError)
                        {
                            entry.Error = commitError.Message; RemoveQueued(entry); ScheduleClose(entry);
                            Report(commitError.Message, ScreenLuaIssueKind.Handler, entry.Instance.Id);
                        }
                    }
                    else Detach(entry);
                    if (entry.Closing) ScheduleClose(entry);
                }
            }
            catch (QueueExpiredException error) { Expire(ready.Work, error.Message); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException)
            { Fail(error.Message); }
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
        else if (queue.First is { } first) { work = first.Value; queue.RemoveFirst(); }
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
        var eventDefinition = work.Event is null ? definition : stack.VisualDefinition(entry.Instance);
        var task = Task.Run(() => PrepareAsync(context, captured, stamp, definition, eventDefinition, clock));
        ScreenLuaConnection.Observe(task);
        pending = new(work, stamp, task);
    }

    private static async Task<Prepared> PrepareAsync(Conversation conversation, Work work, ScreenLuaStamp stamp,
        ScreenDefinition definition, ScreenDefinition eventDefinition, TimeProvider clock)
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
            work.Kind == "load" ? definition.Nodes : [], work.Kind == "load" ? entry.Source : null,
            work.Kind == "load" ? entry.References : ImmutableDictionary<string, Guid>.Empty, work.Event, work.Snapshot);
        exchangeStarted = true;
        var exchange = conversation.Connection.ExchangeAsync(request, token);
        ScreenLuaConnection.Observe(exchange);
        var result = await exchange.WaitAsync(token).ConfigureAwait(false);
        CheckDeadline();
        ScreenLuaConnection.Validate(request, result);
        var batch = result.Error is null && work.Kind != "close" ? ScreenLuaProtocol.Prepare(definition, result.Changes) : null;
        CheckDeadline();
        return new(result, batch);
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
        if (work.Kind == "load") { entry.Error = "Истёк срок загрузки экрана."; RemoveQueued(entry); }
        if (entry.Closing) ScheduleClose(entry);
    }
    private void RemoveQueued(Entry entry)
    {
        var node = queue.First;
        while (node is not null) { var next = node.Next; if (ReferenceEquals(node.Value.Entry, entry)) queue.Remove(node); node = next; }
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
        if (pending?.Work.Entry != entry) ScheduleClose(entry);
    }
    private void Reconcile()
    {
        foreach (var entry in entries.Values.ToArray()) if (!Current(entry.Instance)) Detach(entry);
    }
    private void Report(string message, ScreenLuaIssueKind kind = ScreenLuaIssueKind.Conversation, Guid? instanceId = null)
    {
        if (diagnostics.Count == 32) diagnostics.Dequeue();
        if (issues.Count == 32) issues.Dequeue();
        var text = message.Length > 4096 ? message[..4096] : message;
        diagnostics.Enqueue(text);
        issues.Enqueue(new(kind, instanceId, text));
    }
    private void Fail(string message)
    {
        faulted = true;
        foreach (var entry in entries.Values) { entry.Error = message; entry.Loaded = false; }
        queue.Clear(); closes.Clear();
        Report(message);
        Retire();
    }
    private void Retire()
    {
        conversation.Cancellation.Cancel();
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
        entries.Clear(); queue.Clear(); closes.Clear(); diagnostics.Clear(); issues.Clear();
        Generation++; faulted = false;
        conversation = new(factory());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Retire();
        entries.Clear(); queue.Clear(); closes.Clear();
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
