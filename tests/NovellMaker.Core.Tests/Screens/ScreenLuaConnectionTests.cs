using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenLuaConnectionTests
{
    private sealed class Pipe : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private byte[] current = [];
        private int offset;
        internal void Send(byte[] data) => channel.Writer.TryWrite(data);
        internal void End() => channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            while (offset == current.Length)
            {
                if (!await channel.Reader.WaitToReadAsync(token)) return 0;
                if (channel.Reader.TryRead(out var next)) { current = next; offset = 0; }
            }
            var count = Math.Min(17, Math.Min(buffer.Length, current.Length - offset));
            current.AsMemory(offset, count).CopyTo(buffer); offset += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class ProcessFake : ILuaWorkerProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InputStream input;
        internal readonly Channel<ScreenLuaRequest> Requests = Channel.CreateUnbounded<ScreenLuaRequest>();
        internal readonly Pipe OutputPipe = new(); internal readonly Pipe ErrorPipe = new();
        internal Func<ScreenLuaRequest, ScreenLuaResponse>? Respond = request => ScreenLuaProtocolTests.Reply(request);
        internal bool Limited, Killed, Disposed, WriteBeforeLimits, FailLimits, FailWrite, RefuseExit;
        internal bool OneShot;
        internal bool HoldOneShot;
        internal readonly TaskCompletionSource InputCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessFake() => input = new(this);
        public Stream Input => input;
        public Stream Output => OutputPipe;
        public Stream Error => ErrorPipe;
        public int ExitCode => 0;
        public void ApplyLimits() { if (FailLimits) throw new IOException("limit error"); Limited = true; }
        public void CompleteInput()
        {
            if (!OneShot) throw new InvalidOperationException("Persistent stdin must remain open.");
            InputCompleted.TrySetResult();
            if (HoldOneShot) return;
            OutputPipe.Send(Encoding.UTF8.GetBytes("{\"Error\":null,\"Commands\":[]}"));
            OutputPipe.End(); ErrorPipe.End(); exit.TrySetResult();
        }
        public Task WaitForExitAsync(CancellationToken token) => exit.Task.WaitAsync(token);
        internal async Task<ScreenLuaRequest> Next() => await Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        internal void Reply(ScreenLuaResponse response)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ScreenLuaProtocol.Json);
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            OutputPipe.Send([.. header, .. bytes]);
        }
        internal void Exit() { exit.TrySetResult(); OutputPipe.End(); ErrorPipe.End(); }
        public void Kill() { Killed = true; if (!RefuseExit) Exit(); }
        public void Dispose() { Disposed = true; input.Dispose(); }
        private sealed class InputStream(ProcessFake owner) : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                owner.WriteBeforeLimits |= !owner.Limited;
                return owner.FailWrite ? ValueTask.FromException(new IOException("write error")) : base.WriteAsync(buffer, token);
            }
            public override Task FlushAsync(CancellationToken token)
            {
                var bytes = ToArray(); var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                var request = JsonSerializer.Deserialize<ScreenLuaRequest>(bytes.AsSpan(4, count), ScreenLuaProtocol.Json)!;
                owner.Requests.Writer.TryWrite(request);
                SetLength(0); Position = 0;
                if (owner.Respond is { } reply) owner.Reply(reply(request));
                return Task.CompletedTask;
            }
        }
    }
    private static ScreenLuaConnection Connection(ProcessFake process, SemaphoreSlim? processes = null, SemaphoreSlim? sessions = null,
        TimeSpan? timeout = null, SemaphoreSlim? capacity = null) => new(() => process, processes ?? new(2), sessions ?? new(1), timeout, capacity ?? new(34));

    [TestMethod]
    public async Task PersistentAdmissionStillAllowsExactlyThirtyTwoWaitingV1Requests()
    {
        using var process = new ProcessFake(); using var held = new ProcessFake { OneShot = true, HoldOneShot = true };
        var slots = new SemaphoreSlim(2); var capacity = new SemaphoreSlim(34);
        await using var connection = Connection(process, slots, capacity: capacity);
        await connection.ExchangeAsync(ScreenLuaProtocolTests.Request("handshake"), CancellationToken.None);
        Assert.AreEqual(33, capacity.CurrentCount);
        var transport = new LuaWorkerTransport(() => held, sharedWorkers: slots, sharedCapacity: capacity);
        using var cancellation = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, 33).Select(_ => transport.InvokeAsync(new("syntax", "return 1", "v1.lua"), cancellation.Token)).ToArray();
        await held.InputCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains((await transport.InvokeAsync(new("syntax", "return 1", "v1.lua"))).Error!, "заполнена");
        Assert.AreEqual(0, capacity.CurrentCount);
        cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.AreEqual(33, capacity.CurrentCount);
        await connection.DisposeAsync(); Assert.AreEqual(34, capacity.CurrentCount);
    }

    [TestMethod]
    public async Task FullSharedAdmissionNeverStartsPersistentChild()
    {
        using var process = new ProcessFake(); var sessions = new SemaphoreSlim(1);
        await using var connection = Connection(process, sessions: sessions, capacity: new(0));
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None));
        StringAssert.Contains(error.Message, "заполнена"); Assert.IsFalse(process.Limited); Assert.AreEqual(1, sessions.CurrentCount);
    }

    [TestMethod]
    public async Task PersistentFramedPipeReusesOneLimitedProcessAndDisposeReapsIt()
    {
        using var process = new ProcessFake(); var slots = new SemaphoreSlim(2); var sessions = new SemaphoreSlim(1);
        var connection = Connection(process, slots, sessions);
        for (var i = 0; i < 10; i++)
        {
            var request = ScreenLuaProtocolTests.Request(i == 0 ? "handshake" : "event");
            Assert.AreEqual(request.Stamp, (await connection.ExchangeAsync(request, CancellationToken.None)).Stamp);
        }
        Assert.IsTrue(process.Limited); Assert.IsFalse(process.WriteBeforeLimits); Assert.IsFalse(process.Disposed);
        Assert.AreEqual(1, slots.CurrentCount); Assert.AreEqual(0, sessions.CurrentCount);
        await connection.DisposeAsync(); await connection.DisposeAsync();
        Assert.IsTrue(process.Killed); Assert.IsTrue(process.Disposed);
        Assert.AreEqual(2, slots.CurrentCount); Assert.AreEqual(1, sessions.CurrentCount);
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task PersistentWorkerReservesOnlyOneOfSharedProcessSlotsAndV1StillWorks()
    {
        using var persistent = new ProcessFake(); using var other = new ProcessFake(); using var v1 = new ProcessFake { OneShot = true };
        var slots = new SemaphoreSlim(2); var sessions = new SemaphoreSlim(1);
        await using var first = Connection(persistent, slots, sessions);
        await first.ExchangeAsync(ScreenLuaProtocolTests.Request("handshake"), CancellationToken.None);
        await using var denied = Connection(other, slots, sessions);
        await Assert.ThrowsExactlyAsync<IOException>(() => denied.ExchangeAsync(ScreenLuaProtocolTests.Request("handshake"), CancellationToken.None));
        Assert.IsFalse(other.Limited);
        var oneShot = new LuaWorkerTransport(() => v1, sharedWorkers: slots);
        Assert.IsNull((await oneShot.InvokeAsync(new("syntax", "return 1", "v1.lua"))).Error);
        Assert.AreEqual(1, slots.CurrentCount);
        await first.DisposeAsync(); Assert.AreEqual(2, slots.CurrentCount);
    }

    [TestMethod]
    [DataRow("limits")][DataRow("write")][DataRow("json")][DataRow("oversize")][DataRow("stderr")][DataRow("exit")]
    public async Task BrokenPipesProtocolAndResourceFailuresKillProcessAndReleaseAdmission(string fault)
    {
        using var process = new ProcessFake { Respond = null, FailLimits = fault == "limits", FailWrite = fault == "write" };
        var slots = new SemaphoreSlim(2); var sessions = new SemaphoreSlim(1);
        await using var connection = Connection(process, slots, sessions);
        var task = connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None);
        if (fault is not ("limits" or "write"))
        {
            await process.Next();
            switch (fault)
            {
                case "json": process.OutputPipe.Send([4, 0, 0, 0, 120, 120, 120, 120]); break;
                case "oversize": process.OutputPipe.Send([0, 0, 32, 0]); break;
                case "stderr": process.ErrorPipe.Send(new byte[8193]); break;
                default: process.Exit(); break;
            }
        }
        await Assert.ThrowsAsync<Exception>(() => task);
        Assert.IsTrue(process.Killed); Assert.IsTrue(process.Disposed); Assert.IsFalse(process.WriteBeforeLimits);
        Assert.AreEqual(2, slots.CurrentCount); Assert.AreEqual(1, sessions.CurrentCount);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task CancellationAndTimeoutStopPendingIoWithoutLeavingSlots(bool timeout)
    {
        using var process = new ProcessFake { Respond = null }; var slots = new SemaphoreSlim(2);
        await using var connection = Connection(process, slots, timeout: timeout ? TimeSpan.FromMilliseconds(200) : null);
        using var cancellation = new CancellationTokenSource();
        var task = connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), cancellation.Token);
        await process.Next();
        Assert.IsFalse(task.IsCompleted);
        if (timeout) await Assert.ThrowsExactlyAsync<IOException>(() => task);
        else { cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => task); }
        Assert.IsTrue(process.Killed); Assert.IsTrue(process.Disposed); Assert.AreEqual(2, slots.CurrentCount);
    }

    [TestMethod]
    public async Task DeadlineWhileWaitingForProcessSlotNeverStartsChildOrLeaksSessionSlot()
    {
        using var process = new ProcessFake(); var slots = new SemaphoreSlim(0); var sessions = new SemaphoreSlim(1);
        await using var connection = Connection(process, slots, sessions, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExactlyAsync<IOException>(() => connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None));
        Assert.IsFalse(process.Limited); Assert.AreEqual(1, sessions.CurrentCount); Assert.AreEqual(0, slots.CurrentCount);
    }

    [TestMethod]
    public async Task DisposeDuringActiveExchangeCancelsItAndConfirmsCleanup()
    {
        using var process = new ProcessFake { Respond = null }; var connection = Connection(process);
        var task = connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None); await process.Next();
        await connection.DisposeAsync(); await Assert.ThrowsAsync<Exception>(() => task);
        Assert.IsTrue(process.Killed); Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task UnconfirmedExitHasExplicitBoundedCleanupError()
    {
        using var process = new ProcessFake { Respond = null, RefuseExit = true };
        await using var connection = Connection(process, timeout: TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None));
        StringAssert.Contains(error.Message, "Не подтверждено"); Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task MissingExecutableOrFactoryFailureReleasesAllPermits()
    {
        var slots = new SemaphoreSlim(2); var sessions = new SemaphoreSlim(1);
        await using var connection = new ScreenLuaConnection(() => throw new IOException("start failed"), slots, sessions);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => connection.ExchangeAsync(ScreenLuaProtocolTests.Request(), CancellationToken.None));
        StringAssert.Contains(error.Message, "start failed"); Assert.AreEqual(2, slots.CurrentCount); Assert.AreEqual(1, sessions.CurrentCount);
    }

    [TestMethod]
    public async Task RealWorkerCancellationReapsActualLimitedChildBeforeCompleting()
    {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new ScreenLuaConnection(() => new HeldProcess(new LuaWorkerProcess(LuaWorkerClient.Host(), "--screen-lua-worker"), started), new(2), new(1));
        using var cancellation = new CancellationTokenSource();
        var task = connection.ExchangeAsync(ScreenLuaProtocolTests.Request("handshake"), cancellation.Token);
        using var child = Process.GetProcessById(await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(child.HasExited); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.IsTrue(child.HasExited);
    }
    private sealed class HeldProcess(LuaWorkerProcess inner, TaskCompletionSource<int> started) : ILuaWorkerProcess
    {
        public Stream Input { get; } = new HeldInput();
        public Stream Output => inner.Output;
        public Stream Error => inner.Error;
        public int ExitCode => inner.ExitCode;
        public void ApplyLimits() { inner.ApplyLimits(); started.SetResult(inner.Id); }
        public void CompleteInput() => inner.CompleteInput();
        public Task WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);
        public void Kill() => inner.Kill();
        public void Dispose() { Input.Dispose(); inner.Dispose(); }
        private sealed class HeldInput : MemoryStream
        {
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
