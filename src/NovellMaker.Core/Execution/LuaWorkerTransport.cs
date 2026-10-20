using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Execution;

/// <summary>One deadline covers admission, serialization and RPC. All expensive work runs off the caller thread.</summary>
internal sealed class LuaWorkerTransport
{
    private readonly Func<ILuaWorkerProcess> start;
    private readonly SemaphoreSlim workers;
    private readonly SemaphoreSlim capacity;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };

    internal LuaWorkerTransport(Func<ILuaWorkerProcess> start, TimeProvider? clock = null,
        int maximumWorkers = LuaWorkerClient.MaximumWorkers, int maximumPending = LuaWorkerClient.MaximumPendingRequests,
        SemaphoreSlim? sharedWorkers = null, SemaphoreSlim? sharedCapacity = null)
    {
        this.start = start;
        this.clock = clock ?? TimeProvider.System;
        timeout = TimeSpan.FromMilliseconds(LuaWorkerClient.TimeoutMilliseconds);
        workers = sharedWorkers ?? new(maximumWorkers);
        capacity = sharedCapacity ?? new(checked(maximumWorkers + maximumPending));
    }

    internal Task<LuaWorkerResponse> InvokeAsync(LuaWorkerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokePreparedAsync(_ => request, cancellationToken);
    }

    internal async Task<LuaWorkerResponse> InvokePreparedAsync(Func<LuaRequestPreparation, LuaWorkerRequest> createRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createRequest);
        cancellationToken.ThrowIfCancellationRequested();
        var submittedAt = clock.GetTimestamp();
        if (!capacity.Wait(0)) return new("Очередь изолированного Lua заполнена. Повторите запрос позже.", []);
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var admitted = false;
        try
        {
            await workers.WaitAsync(operation.Token).ConfigureAwait(false);
            admitted = true;
            CheckDeadline(submittedAt, operation.Token);
            // Process.Start and bounded JSON serialization can be synchronous/expensive.
            var response = await Task.Run(() => ExchangeAsync(createRequest, submittedAt, operation.Token), operation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckDeadline(submittedAt, operation.Token);
            return response;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("Изолированный Lua остановлен: превышен общий таймаут 5 секунд.", []);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or NotSupportedException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("Изолированный Lua остановлен (ошибка процесса или протокола): " + error.Message, []);
        }
        finally
        {
            if (admitted) workers.Release();
            capacity.Release();
        }
    }

    private void CheckDeadline(long submittedAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Timer callbacks can be delayed by scheduling; an expired waiter must not start a child.
        if (clock.GetElapsedTime(submittedAt) >= timeout) throw new OperationCanceledException(token);
    }

    private async Task<LuaWorkerResponse> ExchangeAsync(Func<LuaRequestPreparation, LuaWorkerRequest> createRequest, long submittedAt, CancellationToken token)
    {
        CheckDeadline(submittedAt, token);
        var request = createRequest(new(token, () => CheckDeadline(submittedAt, token)));
        CheckDeadline(submittedAt, token);
        using var buffer = new LuaWorkerClient.BoundedBuffer(LuaWorkerClient.MaximumRequestBytes);
        await JsonSerializer.SerializeAsync(buffer, request, cancellationToken: token).ConfigureAwait(false);
        CheckDeadline(submittedAt, token);
        using var process = start();
        using var io = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new List<Task>();
        try
        {
            // No project code is written until the OS memory limit has been installed.
            process.ApplyLimits();
            CheckDeadline(submittedAt, token);
            var output = LuaWorkerClient.ReadBoundedAsync(process.Output, LuaWorkerClient.MaximumResponseBytes, io.Token);
            var errors = LuaWorkerClient.ReadBoundedAsync(process.Error, 8192, io.Token);
            pending.Add(output);
            pending.Add(errors);
            pending.Add(WriteRequestAsync(process, buffer, io.Token));
            pending.Add(process.WaitForExitAsync(io.Token));
            // WhenAll alone would delay an oversized/broken pipe until the other pipes exit.
            var remaining = pending.ToList();
            while (remaining.Count != 0)
            {
                var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                remaining.Remove(completed);
            }
            CheckDeadline(submittedAt, token);
            if (process.ExitCode != 0)
                return new($"Изолированный Lua завершён аварийно (код {process.ExitCode}). Команды отклонены. " + Encoding.UTF8.GetString(await errors.ConfigureAwait(false)), []);
            var response = JsonSerializer.Deserialize<LuaWorkerResponse>(await output.ConfigureAwait(false), ResponseOptions);
            if (response is null || response.Commands.IsDefault || response.Commands.Length > LuaActionScript.MaximumCommands ||
                response.Commands.Any(a => a is null) || response.Error is { Length: > 4096 } || response.Error is not null && !response.Commands.IsEmpty)
                throw new IOException("Некорректный ответ Lua worker.");
            CheckDeadline(submittedAt, token);
            return response;
        }
        finally
        {
            io.Cancel();
            // No unbounded wait on a child or pipe, even after timeout/cancellation.
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                process.Kill();
                await process.WaitForExitAsync(cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) { throw new IOException("Не удалось подтвердить завершение Lua worker за отведённое время очистки.", error); }
            finally
            {
                var all = Task.WhenAll(pending);
                try { await all.WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception)
                {
                    // Observe late faults as well when an OS pipe does not honour cancellation.
                    _ = all.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }

    private static async Task WriteRequestAsync(ILuaWorkerProcess process, MemoryStream buffer, CancellationToken token)
    {
        buffer.Position = 0;
        await buffer.CopyToAsync(process.Input, token).ConfigureAwait(false);
        process.CompleteInput();
    }
}
