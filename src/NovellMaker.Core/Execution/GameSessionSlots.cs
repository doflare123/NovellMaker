using System.Collections.Immutable;

namespace NovellMaker.Core.Execution;

public enum GameSlotOperation { Save, Load, Continue, List, Delete }
public sealed record GameSlotResult(GameSlotOperation Operation, bool Success, string Message,
    ImmutableArray<GameSlotInfo> Slots = default);

/// <summary>Host-thread coordinator: capture before starting I/O, poll and commit only in the originating lifetime.</summary>
public sealed class GameSessionSlots : IDisposable, IAsyncDisposable
{
    private readonly GameSession session;
    private readonly IGameSlotStore store;
    private sealed record Reply(GameSlotRead? Read = null, ImmutableArray<GameSlotInfo> Slots = default, PreparedGameLoad? Load = null);
    private sealed record Pending(long Generation, GameSlotOperation Operation, CancellationTokenSource Cancellation, Task<Reply> Task);
    private Pending? pending;
    private readonly List<Task> retiring = [];
    private bool disposed;
    public bool IsBusy => pending is not null;
    public GameSlotResult? LastResult { get; private set; }

    public GameSessionSlots(GameSession session, IGameSlotStore store)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        session.Resetting += Cancel;
    }

    public bool Save(string id = GameSlotStore.QuickSlotId, string? title = null) => Start(GameSlotOperation.Save, token =>
    {
        // This delegate executes synchronously before any await, on the calling host thread.
        var snapshot = GameSlotStore.Capture(session, title);
        return SaveSnapshot(snapshot, token);
        async Task<Reply> SaveSnapshot(GameSlotCapture captured, CancellationToken cancellation)
        {
            var prepared = await GameSlotStore.PrepareAsync(captured, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await store.SaveAsync(id, prepared, cancellation).ConfigureAwait(false);
            return new();
        }
    });

    public bool Load(string id = GameSlotStore.QuickSlotId) => StartLoad(GameSlotOperation.Load, async token => await store.ReadAsync(id, token).ConfigureAwait(false));
    public bool Continue() => StartLoad(GameSlotOperation.Continue, token => store.LatestAsync(token));
    private bool StartLoad(GameSlotOperation operation, Func<CancellationToken, Task<GameSlotRead?>> read) => Start(operation, token =>
    {
        var preparation = session.CaptureLoadPreparation(); // Host capture before the first await.
        return PrepareReply();
        async Task<Reply> PrepareReply()
        {
            var result = await read(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var prepared = result is { Info.Status: GameSlotStatus.Compatible, Snapshot: { } snapshot }
                ? await preparation.PrepareAsync(snapshot.SaveJson, token).ConfigureAwait(false) : null;
            return new(result, Load: prepared);
        }
    });
    public bool List() => Start(GameSlotOperation.List,
        async token => new(Slots: await store.ListAsync(token).ConfigureAwait(false)));
    public bool Delete(string id) => Start(GameSlotOperation.Delete, async token =>
    { await store.DeleteAsync(id, token).ConfigureAwait(false); return new(); });

    private bool Start(GameSlotOperation operation, Func<CancellationToken, Task<Reply>> begin)
    {
        if (disposed || pending is not null || !session.IsCurrentGeneration(session.Generation)) return false;
        var generation = session.Generation; // Never read the generation for the first time at completion.
        var cancellation = new CancellationTokenSource();
        LastResult = null;
        try { pending = new(generation, operation, cancellation, begin(cancellation.Token)); return true; }
        catch (Exception error) when (Expected(error))
        { cancellation.Dispose(); LastResult = new(operation, false, error.Message); return false; }
    }

    /// <summary>Call from Update. Never blocks; a stale response cannot alter state, screens or feedback.</summary>
    public bool Poll()
    {
        if (pending is not { Task.IsCompleted: true } work) return false;
        pending = null;
        Reply? reply = null;
        try
        {
            reply = work.Task.GetAwaiter().GetResult(); // Completed task only.
            if (disposed || work.Cancellation.IsCancellationRequested || !session.IsCurrentGeneration(work.Generation))
                return false;
            if (work.Operation is GameSlotOperation.Load or GameSlotOperation.Continue)
            {
                if (reply.Read is not { Info.Status: GameSlotStatus.Compatible, Snapshot: { } snapshot })
                {
                    if (reply.Read?.Info.Status == GameSlotStatus.Unavailable) throw new IOException("Файл слота недоступен.");
                    throw new InvalidOperationException(reply.Read?.Info.Diagnostic ?? "Совместимых сохранений нет.");
                }
                if (reply.Load is null || !session.TryLoadPrepared(reply.Load, work.Generation)) return false;
            }
            LastResult = new(work.Operation, true, work.Operation switch
            {
                GameSlotOperation.Save => "Игра сохранена",
                GameSlotOperation.Load or GameSlotOperation.Continue => "Игра загружена",
                GameSlotOperation.Delete => "Слот удалён",
                _ => "Список слотов обновлён",
            }, reply.Slots);
            return true;
        }
        catch (Exception error) when (Expected(error))
        {
            if (disposed || session.Generation != work.Generation || work.Cancellation.IsCancellationRequested) return false;
            LastResult = new(work.Operation, false, error is IOException or UnauthorizedAccessException
                ? "Хранилище слотов недоступно. Проверьте доступ к файлам и свободное место." : error.Message);
            return true;
        }
        finally { reply?.Load?.Dispose(); work.Cancellation.Dispose(); }
    }

    /// <summary>Invalidate a host-owned operation before Poll can apply it. A published disk write is not rolled back.</summary>
    public void Cancel()
    {
        LastResult = null;
        if (pending is not { } work) return;
        pending = null;
        try { work.Cancellation.Cancel(); }
        finally
        {
            retiring.RemoveAll(task => task.IsCompleted);
            retiring.Add(work.Task.ContinueWith(task =>
            {
                _ = task.Exception; // Observe even a deliberately cancellation-ignoring backend's late failure.
                if (task.IsCompletedSuccessfully) task.Result.Load?.Dispose();
                work.Cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; session.Resetting -= Cancel; Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await Task.WhenAll(retiring).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { } // Late completion remains observed; shutdown has a bounded wait.
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or OperationCanceledException;
}
