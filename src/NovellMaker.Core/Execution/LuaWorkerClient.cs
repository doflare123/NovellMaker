using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Execution;

public sealed record LuaWorkerRequest(string Mode, string Source, string Name, ScriptAction? Action = null, GameState? State = null, NovelProject? Project = null);
public sealed record LuaWorkerResponse(string? Error, ImmutableArray<ScenarioAction> Commands);

/// <summary>No MoonSharp parser or VM executes in the editor/game process.</summary>
public static class LuaWorkerClient
{
    public const int MaximumRequestBytes = 16 * 1024 * 1024;
    public const int MaximumResponseBytes = 1024 * 1024;
    public const int TimeoutMilliseconds = 5000;
    public const int MaximumWorkers = 2;
    public const int MaximumPendingRequests = 32;
    private static readonly LuaWorkerTransport Transport = new(() => new LuaWorkerProcess(Host()));

    /// <summary>Bounded isolated RPC. Caller cancellation throws; timeout/protocol/process failures return an error and no commands.</summary>
    public static Task<LuaWorkerResponse> InvokeAsync(LuaWorkerRequest request, CancellationToken cancellationToken = default) =>
        Transport.InvokeAsync(request, cancellationToken);

    private static string Host()
    {
        var executable = OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime";
        foreach (var folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "Runtime"), Path.Combine(AppContext.BaseDirectory, "LuaWorker") })
        {
            var candidate = Path.Combine(folder, executable);
            if (File.Exists(candidate) && File.Exists(Path.Combine(folder, "NovellMaker.Runtime.dll")) && File.Exists(Path.Combine(folder, "NovellMaker.Runtime.runtimeconfig.json"))) return candidate;
        }
        throw new IOException("Не найден поставляемый runtime для изолированного Lua. Пересоберите поставку.");
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > maximum) throw new IOException("Превышен размер сообщения Lua worker.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    internal sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count) { if (Length + count > maximum) throw new IOException("Снимок для изолированного Lua превышает 16 МиБ."); }
    }
}

/// <summary>Called exclusively by the runtime's --lua-worker entry point, before any game/UI initialization.</summary>
public static class LuaWorkerEntry
{
    public static int Run()
    {
        try
        {
            var bytes = LuaWorkerClient.ReadBoundedAsync(Console.OpenStandardInput(), LuaWorkerClient.MaximumRequestBytes, CancellationToken.None).GetAwaiter().GetResult();
            var request = JsonSerializer.Deserialize<LuaWorkerRequest>(bytes) ?? throw new ArgumentException("Нет запроса Lua.");
            LuaWorkerResponse result;
            try
            {
                LuaLimits.CheckSource(request.Source);
                if (request.Mode == "syntax") { LuaActionScript.ValidateInWorker(request.Source, request.Name); result = new(null, []); }
                else if (request.Mode == "run" && request.Action is { } action && request.State is { } state && request.Project is { } project)
                    result = new(null, LuaActionScript.RunInWorker(action, state, project));
                else throw new ArgumentException("Неизвестный запрос Lua.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { result = new(error.Message.Length > 4096 ? error.Message[..4096] : error.Message, []); }
            var output = JsonSerializer.SerializeToUtf8Bytes(result);
            if (output.Length > LuaWorkerClient.MaximumResponseBytes) output = JsonSerializer.SerializeToUtf8Bytes(new LuaWorkerResponse("Превышен размер результата Lua.", []));
            Console.OpenStandardOutput().Write(output);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { Console.Error.WriteLine(error.Message); return 2; }
    }
}
