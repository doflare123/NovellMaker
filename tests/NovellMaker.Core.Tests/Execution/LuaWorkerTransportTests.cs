using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class LuaWorkerTransportTests
{
    private static readonly LuaWorkerRequest Request = new("syntax", "return 1", "transport.lua");
    private const string Success = "{\"Error\":null,\"Commands\":[]}";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Guard<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Guard(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    [TestMethod]
    public async Task DelayedWorkerDoesNotBlockCallerAndReceivesInputOnlyAfterLimits()
    {
        using var process = new FakeProcess();
        var transport = new LuaWorkerTransport(() => process);
        var task = transport.InvokeAsync(Request);
        await Guard(process.InputCompleted.Task);
        Assert.IsFalse(task.IsCompleted);
        Assert.IsTrue(process.LimitsApplied);
        Assert.IsTrue(process.AllWritesAfterLimits);
        Assert.AreEqual(JsonSerializer.Serialize(Request), Encoding.UTF8.GetString(process.Written));
        process.Finish();
        var result = await Guard(task);
        Assert.IsNull(result.Error, result.Error);
        Assert.IsEmpty(result.Commands);
        Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task AdmissionIsBoundedAndCancelledWaiterDoesNotLeakPermits()
    {
        using var first = new FakeProcess();
        using var replacement = new FakeProcess();
        var queue = new ConcurrentQueue<ILuaWorkerProcess>([first, replacement]);
        var starts = 0;
        var transport = new LuaWorkerTransport(() => { Interlocked.Increment(ref starts); return Dequeue(queue); }, maximumWorkers: 1, maximumPending: 1);
        var active = transport.InvokeAsync(Request);
        await Guard(first.InputCompleted.Task);
        using var cancellation = new CancellationTokenSource();
        var waiting = transport.InvokeAsync(Request, cancellation.Token);
        var busy = await Guard(transport.InvokeAsync(Request));
        StringAssert.Contains(busy.Error!, "заполнена");
        Assert.AreEqual(1, starts);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Guard(waiting));
        var next = transport.InvokeAsync(Request);
        first.Finish();
        Assert.IsNull((await Guard(active)).Error);
        await Guard(replacement.InputCompleted.Task);
        replacement.Finish();
        Assert.IsNull((await Guard(next)).Error);
        Assert.AreEqual(2, starts);
    }

    [TestMethod]
    public async Task GlobalAdmissionAllowsExactlyTwoActiveAndThirtyTwoWaiting()
    {
        var clock = new ManualClock();
        using var first = new FakeProcess();
        using var second = new FakeProcess();
        var queue = new ConcurrentQueue<ILuaWorkerProcess>([first, second]);
        var starts = 0;
        var transport = new LuaWorkerTransport(() => { Interlocked.Increment(ref starts); return Dequeue(queue); }, clock);
        var requests = Enumerable.Range(0, LuaWorkerClient.MaximumWorkers + LuaWorkerClient.MaximumPendingRequests)
            .Select(_ => transport.InvokeAsync(Request)).ToArray();
        await Guard(first.InputCompleted.Task);
        await Guard(second.InputCompleted.Task);
        StringAssert.Contains((await Guard(transport.InvokeAsync(Request))).Error!, "заполнена");
        Assert.AreEqual(2, starts);
        clock.Advance(TimeSpan.FromSeconds(5));
        foreach (var result in await Guard(Task.WhenAll(requests)))
        {
            StringAssert.Contains(result.Error!, "таймаут");
            Assert.IsEmpty(result.Commands);
        }
        Assert.AreEqual(2, starts, "Expired queued work must never start a child.");
        Assert.IsTrue(first.Killed && second.Killed);
    }

    [TestMethod]
    public async Task QueueAndExecutionShareOneDeadlineWithoutResetAtProcessStart()
    {
        var clock = new ManualClock();
        using var first = new FakeProcess();
        using var second = new FakeProcess();
        var queue = new ConcurrentQueue<ILuaWorkerProcess>([first, second]);
        var transport = new LuaWorkerTransport(() => Dequeue(queue), clock, maximumWorkers: 1);
        var active = transport.InvokeAsync(Request);
        await Guard(first.InputCompleted.Task);
        var waiting = transport.InvokeAsync(Request);
        clock.Advance(TimeSpan.FromSeconds(4));
        first.Finish();
        Assert.IsNull((await Guard(active)).Error);
        await Guard(second.InputCompleted.Task);
        clock.Advance(TimeSpan.FromSeconds(1));
        StringAssert.Contains((await Guard(waiting)).Error!, "таймаут");
        Assert.IsTrue(second.Killed);
    }

    [TestMethod]
    public async Task CallerCancellationBeforeAdmissionDoesNotStartAWorker()
    {
        var starts = 0;
        var transport = new LuaWorkerTransport(() => { starts++; throw new IOException("must not start"); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => transport.InvokeAsync(Request, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(0, starts);
    }

    [TestMethod]
    public async Task CancellationDuringExchangeKillsWorkerBeforeOperationCompletes()
    {
        using var process = new FakeProcess();
        using var cancellation = new CancellationTokenSource();
        var transport = new LuaWorkerTransport(() => process);
        var task = transport.InvokeAsync(Request, cancellation.Token);
        await Guard(process.InputCompleted.Task);
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Guard(task));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(process.Killed);
        Assert.IsTrue(process.Disposed);
        // A later success cannot resurrect the cancelled operation.
        process.Finish();
        Assert.IsTrue(task.IsCanceled);
    }

    [TestMethod]
    public async Task CancellationDuringCleanupCannotReturnAnEarlierSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        using var process = new FakeProcess { OnKill = cancellation.Cancel };
        process.Finish();
        var task = new LuaWorkerTransport(() => process).InvokeAsync(Request, cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Guard(task));
        Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task CleanupHasBoundedWaitAndReportsFailureInsteadOfSuccess()
    {
        using var process = new FakeProcess { RefuseExit = true };
        var transport = new LuaWorkerTransport(() => process, new ManualClock());
        var task = transport.InvokeAsync(Request);
        await Guard(process.InputCompleted.Task);
        process.Outputs.Release(new byte[LuaWorkerClient.MaximumResponseBytes + 1]);
        var result = await Guard(task);
        StringAssert.Contains(result.Error!, "время очистки");
        Assert.IsEmpty(result.Commands);
        Assert.IsTrue(process.Killed && process.Disposed);
    }

    [TestMethod]
    public async Task ResponseAndStderrAtExactByteLimitAreAccepted()
    {
        using var process = new FakeProcess();
        process.Finish(Success + new string(' ', LuaWorkerClient.MaximumResponseBytes - Encoding.UTF8.GetByteCount(Success)),
            stderr: new string('x', 8192));
        var result = await Guard(new LuaWorkerTransport(() => process).InvokeAsync(Request));
        Assert.IsNull(result.Error, result.Error);
    }

    [TestMethod]
    public async Task AsyncBufferOverloadsEnforceLimitAtTheBoundary()
    {
        using var buffer = new LuaWorkerClient.BoundedBuffer(3);
        await buffer.WriteAsync(new byte[2], 0, 2, CancellationToken.None);
        await buffer.WriteAsync(new byte[1].AsMemory());
        Assert.AreEqual(3L, buffer.Length);
        await Assert.ThrowsAsync<IOException>(() => buffer.WriteAsync(new byte[1], 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () => await buffer.WriteAsync(new byte[1].AsMemory()));
        Assert.AreEqual(3L, buffer.Length);
    }

    [TestMethod]
    public async Task LimitFailureSendsNoCodeAndStillCleansProcess()
    {
        using var process = new FakeProcess { LimitError = new IOException("job refused") };
        var result = await Guard(new LuaWorkerTransport(() => process).InvokeAsync(Request));
        StringAssert.Contains(result.Error!, "job refused");
        Assert.IsEmpty(result.Commands);
        Assert.IsEmpty(process.Written);
        Assert.IsTrue(process.Killed && process.Disposed);
    }

    [TestMethod]
    public async Task BrokenInputFailsImmediatelyAndReleasesWorkerSlot()
    {
        using var broken = new FakeProcess { WriteError = new IOException("broken stdin") };
        using var healthy = new FakeProcess();
        healthy.Finish();
        var queue = new ConcurrentQueue<ILuaWorkerProcess>([broken, healthy]);
        var transport = new LuaWorkerTransport(() => Dequeue(queue), new ManualClock(), maximumWorkers: 1);
        StringAssert.Contains((await Guard(transport.InvokeAsync(Request))).Error!, "broken stdin");
        Assert.IsTrue(broken.Killed && broken.Disposed);
        Assert.IsNull((await Guard(transport.InvokeAsync(Request))).Error);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedPipeFailsBeforeWorkerExitOrDeadline(bool stderr)
    {
        using var process = new FakeProcess();
        var transport = new LuaWorkerTransport(() => process, new ManualClock());
        var task = transport.InvokeAsync(Request);
        await Guard(process.InputCompleted.Task);
        (stderr ? process.Errors : process.Outputs).Release(new byte[(stderr ? 8192 : LuaWorkerClient.MaximumResponseBytes) + 1]);
        var result = await Guard(task);
        StringAssert.Contains(result.Error!, "размер сообщения");
        Assert.IsTrue(process.Killed && process.Disposed);
        Assert.IsEmpty(result.Commands);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"Error\":null,\"Commands\":null}")]
    [DataRow("{\"Error\":null,\"Commands\":[],\"Future\":1}")]
    [DataRow("{\"Error\":null,\"Error\":null,\"Commands\":[]}")]
    [DataRow("{\"Error\":null,\"Commands\":[null]}")]
    [DataRow("{\"Error\":null,\"Commands\":[{\"kind\":\"unknown\"}]}")]
    public async Task MalformedResponseNeverReturnsCommands(string json)
    {
        using var process = new FakeProcess();
        process.Finish(json);
        var result = await Guard(new LuaWorkerTransport(() => process).InvokeAsync(Request));
        Assert.IsNotNull(result.Error);
        Assert.IsEmpty(result.Commands);
        Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task InvalidEnvelopeQuotasAndErrorWithCommandsAreRejected()
    {
        var command = new SetAction(Guid.NewGuid(), "x", "y");
        foreach (var response in new[] {
            new LuaWorkerResponse(new string('x', 4097), []),
            new LuaWorkerResponse("failure", [command]),
            new LuaWorkerResponse(null, [.. Enumerable.Repeat<ScenarioAction>(command, 257)]) })
        {
            using var process = new FakeProcess();
            process.Finish(JsonSerializer.Serialize(response));
            var result = await Guard(new LuaWorkerTransport(() => process).InvokeAsync(Request));
            Assert.IsNotNull(result.Error);
            Assert.IsEmpty(result.Commands);
        }
    }

    [TestMethod]
    public async Task WorkerDiagnosticAndAbnormalExitBothDiscardCommands()
    {
        using var failed = new FakeProcess();
        failed.Finish("{\"Error\":\"syntax error\",\"Commands\":[]}");
        Assert.AreEqual("syntax error", (await Guard(new LuaWorkerTransport(() => failed).InvokeAsync(Request))).Error);
        using var crashed = new FakeProcess();
        crashed.Finish(Success, 7, "crash report");
        var result = await Guard(new LuaWorkerTransport(() => crashed).InvokeAsync(Request));
        StringAssert.Contains(result.Error!, "код 7");
        StringAssert.Contains(result.Error!, "crash report");
        Assert.IsEmpty(result.Commands);
    }

    [TestMethod]
    public async Task OversizedSerializedRequestDoesNotLaunchAnyProcess()
    {
        var starts = 0;
        var transport = new LuaWorkerTransport(() => { starts++; throw new IOException("must not start"); });
        var result = await Guard(transport.InvokeAsync(Request with { Source = new string('a', LuaWorkerClient.MaximumRequestBytes) }));
        StringAssert.Contains(result.Error!, "16 МиБ");
        Assert.AreEqual(0, starts);
    }

    [TestMethod]
    public async Task MissingExecutableAndFactoryFailureAreDiagnostic()
    {
        var transport = new LuaWorkerTransport(() => new LuaWorkerProcess(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
        Assert.IsNotNull((await Guard(transport.InvokeAsync(Request))).Error);
        transport = new LuaWorkerTransport(() => throw new IOException("start refused"));
        StringAssert.Contains((await Guard(transport.InvokeAsync(Request))).Error!, "start refused");
    }

    [TestMethod]
    public async Task RealWorkerHandlesAsyncSyntaxRequestAndCancellationKillsActualChild()
    {
        var success = await Guard(LuaWorkerClient.InvokeAsync(Request));
        Assert.IsNull(success.Error, success.Error);
        var syntax = await Guard(LuaWorkerClient.InvokeAsync(Request with { Source = "if then" }));
        Assert.IsNotNull(syntax.Error);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new LuaWorkerTransport(() =>
        {
            var process = new LuaWorkerProcess(Path.Combine(AppContext.BaseDirectory, "LuaWorker",
                OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime"));
            return new HeldRealProcess(process, started);
        });
        var task = transport.InvokeAsync(Request, cancellation.Token);
        var pid = await Guard(started.Task);
        using var child = Process.GetProcessById(pid);
        Assert.IsFalse(child.HasExited);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Guard(task));
        Assert.IsTrue(child.HasExited, "Cancellation must reap the actual worker before returning.");
    }

    private static ILuaWorkerProcess Dequeue(ConcurrentQueue<ILuaWorkerProcess> queue) =>
        queue.TryDequeue(out var process) ? process : throw new IOException("Unexpected child startup.");

    private sealed class FakeProcess : ILuaWorkerProcess
    {
        private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TrackedInput input;
        public FakeProcess() => input = new(this);
        public TaskCompletionSource InputCompleted { get; } = Signal();
        public AsyncPipe Outputs { get; } = new();
        public AsyncPipe Errors { get; } = new();
        public Stream Input => input;
        public Stream Output => Outputs;
        public Stream Error => Errors;
        public int ExitCode => exit.Task.GetAwaiter().GetResult();
        public bool LimitsApplied { get; private set; }
        public bool AllWritesAfterLimits { get; private set; } = true;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public byte[] Written => input.ToArray();
        public Exception? LimitError { get; init; }
        public Exception? WriteError { get; init; }
        public bool RefuseExit { get; init; }
        public Action? OnKill { get; init; }
        public void ApplyLimits() { if (LimitError is not null) throw LimitError; LimitsApplied = true; }
        public void CompleteInput() => InputCompleted.TrySetResult();
        public Task WaitForExitAsync(CancellationToken token) => exit.Task.WaitAsync(token);
        public void Finish(string response = Success, int code = 0, string stderr = "")
        { Outputs.Release(Encoding.UTF8.GetBytes(response)); Errors.Release(Encoding.UTF8.GetBytes(stderr)); exit.TrySetResult(code); }
        public void Kill() { OnKill?.Invoke(); if (!exit.Task.IsCompleted) Killed = true; if (!RefuseExit) exit.TrySetResult(-1); }
        public void Dispose() { Disposed = true; input.Dispose(); Outputs.Dispose(); Errors.Dispose(); }
        private sealed class TrackedInput(FakeProcess owner) : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.AllWritesAfterLimits &= owner.LimitsApplied;
                if (owner.WriteError is { } error) return ValueTask.FromException(error);
                return base.WriteAsync(buffer, cancellationToken);
            }
        }
    }

    private sealed class AsyncPipe : Stream
    {
        private readonly TaskCompletionSource<byte[]> data = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int offset;
        public void Release(byte[] bytes) => data.TrySetResult(bytes);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = await data.Task.WaitAsync(cancellationToken);
            var count = Math.Min(buffer.Length, bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeldRealProcess(LuaWorkerProcess inner, TaskCompletionSource<int> started) : ILuaWorkerProcess
    {
        private readonly HeldInput input = new();
        // Input remains open and unwritten, so the real worker waits before parsing any code.
        public Stream Input => input;
        public Stream Output => inner.Output;
        public Stream Error => inner.Error;
        public int ExitCode => inner.ExitCode;
        public void ApplyLimits() { inner.ApplyLimits(); started.TrySetResult(inner.Id); }
        public void CompleteInput() => inner.CompleteInput();
        public Task WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);
        public void Kill() => inner.Kill();
        public void Dispose() { input.Dispose(); inner.Dispose(); }
        private sealed class HeldInput : MemoryStream
        {
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private TimeSpan now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return now.Ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) { var timer = new ManualTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] ready;
            lock (gate) { now += elapsed; ready = [.. timers.Where(t => !t.Disposed && t.Due <= now)]; foreach (var timer in ready) timer.Disposed = true; }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan Due { get; private set; }
            public bool Disposed { get; set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.gate) { Due = owner.now + dueTime; return !Disposed; } }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
