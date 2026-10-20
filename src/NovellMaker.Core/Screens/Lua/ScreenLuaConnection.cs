using System.Text.Json;
using System.Diagnostics;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Screens.Lua;

internal interface IScreenLuaConnection : IAsyncDisposable
{
    Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token);
}

/// <summary>One framed conversation, serialized calls, bounded lifetime/IO and fail-closed protocol.</summary>
internal sealed class ScreenLuaConnection : IScreenLuaConnection
{
    private readonly Func<ILuaWorkerProcess> start;
    private readonly SemaphoreSlim processes;
    private readonly SemaphoreSlim sessions;
    private readonly SemaphoreSlim capacity;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan timeout;
    private ILuaWorkerProcess? process;
    private Task? exit;
    private Task<byte[]>? errors;
    private bool processSlot;
    private bool sessionSlot;
    private bool requestSlot;
    private bool disposed;

    internal ScreenLuaConnection(Func<ILuaWorkerProcess>? start = null, SemaphoreSlim? processes = null,
        SemaphoreSlim? sessions = null, TimeSpan? timeout = null, SemaphoreSlim? capacity = null)
    {
        this.start = start ?? (() => new LuaWorkerProcess(LuaWorkerClient.Host(), "--screen-lua-worker"));
        this.processes = processes ?? LuaWorkerSlots.Processes;
        this.sessions = sessions ?? LuaWorkerSlots.ScreenSessions;
        this.capacity = capacity ?? LuaWorkerSlots.Requests;
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
    {
        var submitted = Stopwatch.GetTimestamp();
        return Task.Run(() => ExchangeCoreAsync(request, submitted, token), token);
    }

    private async Task<ScreenLuaResponse> ExchangeCoreAsync(ScreenLuaRequest request, long submitted, CancellationToken token)
    {
        var remaining = timeout - Stopwatch.GetElapsedTime(submitted);
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, deadline.Token);
        var ct = operation.Token;
        void CheckDeadline()
        {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(submitted) >= timeout) throw new OperationCanceledException(ct);
        }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckDeadline();
            if (process is null)
            {
                if (!sessions.Wait(0)) throw new IOException("Экранный Lua-сеанс уже занят в этом host-процессе.");
                sessionSlot = true;
                if (!capacity.Wait(0)) throw new IOException("Очередь изолированного Lua заполнена.");
                requestSlot = true;
                await processes.WaitAsync(ct).ConfigureAwait(false);
                processSlot = true;
                CheckDeadline();
                process = start();
                process.ApplyLimits();
                CheckDeadline();
                errors = LuaWorkerClient.ReadBoundedAsync(process.Error, 8192, lifetime.Token);
                exit = process.WaitForExitAsync(lifetime.Token);
            }
            var exchange = RoundTripAsync(process, request, ct);
            Observe(exchange);
            var finished = await Task.WhenAny(exchange, errors!, exit!).WaitAsync(ct).ConfigureAwait(false);
            if (finished != exchange)
            {
                await finished.ConfigureAwait(false);
                throw new IOException("Экранный Lua-worker завершился или закрыл канал диагностики.");
            }
            var response = await exchange.ConfigureAwait(false);
            CheckDeadline();
            Validate(request, response);
            return response;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            lifetime.Cancel();
            await CleanupAsync().ConfigureAwait(false);
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new IOException("Экранный Lua остановлен: отмена сеанса или таймаут 5 секунд.", error);
            throw;
        }
        finally { gate.Release(); }
    }

    private static async Task<ScreenLuaResponse> RoundTripAsync(ILuaWorkerProcess process, ScreenLuaRequest request, CancellationToken token)
    {
        // Read concurrently with writing so neither full pipe can deadlock the peer.
        var read = ScreenLuaProtocol.ReadAsync<ScreenLuaResponse>(process.Output, token);
        var write = ScreenLuaProtocol.WriteAsync(process.Input, request, token);
        Observe(read); Observe(write);
        var first = await Task.WhenAny(read, write).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await Task.WhenAll(read, write).ConfigureAwait(false);
        return await read.ConfigureAwait(false);
    }

    internal static void Validate(ScreenLuaRequest request, ScreenLuaResponse response)
    {
        var expected = request.Kind switch { "handshake" => "ready", "close" => "closed", _ => "batch" };
        if (response.Protocol != ScreenLuaProtocol.Version || response.Stamp != request.Stamp ||
            response.Changes.IsDefault || response.Changes.Length > LuaActionScript.MaximumCommands || response.Changes.Any(c => c is null) ||
            (response.Error is not null ? response.Kind != "error" || response.Error.Length is < 1 or > 4096 || !response.Changes.IsEmpty
                : response.Kind != expected || expected != "batch" && !response.Changes.IsEmpty))
            throw new IOException("Неверный ответ протокола экранного Lua.");
    }

    private async Task CleanupAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (process is not null)
            {
                process.Kill();
                await process.WaitForExitAsync(cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) { throw new IOException("Не подтверждено завершение экранного Lua-worker.", error); }
        finally
        {
            if (exit is not null) Observe(exit);
            if (errors is not null) Observe(errors);
            process?.Dispose(); process = null;
            if (processSlot) { processes.Release(); processSlot = false; }
            if (sessionSlot) { sessions.Release(); sessionSlot = false; }
            if (requestSlot) { capacity.Release(); requestSlot = false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        // A late Process.Start cannot publish data: it rechecks cancellation before any write.
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return;
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
