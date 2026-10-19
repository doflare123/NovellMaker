using System.Collections.Immutable;
using System.Diagnostics;
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
    private static readonly SemaphoreSlim Slots = new(2);

    internal static LuaWorkerResponse Invoke(LuaWorkerRequest request)
    {
        if (!Slots.Wait(TimeoutMilliseconds)) return new("Не удалось получить изолированный процесс Lua: таймаут.", []);
        try { return InvokeAsync(request).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        { return new("Изолированный Lua остановлен (лимит памяти/времени или ошибка процесса): " + error.Message, []); }
        finally { Slots.Release(); }
    }

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

    private static async Task<LuaWorkerResponse> InvokeAsync(LuaWorkerRequest request)
    {
        using var requestBuffer = new BoundedBuffer(MaximumRequestBytes);
        JsonSerializer.Serialize(requestBuffer, request);
        var input = requestBuffer.ToArray();
        var start = new ProcessStartInfo(Host()) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        start.ArgumentList.Add("--lua-worker");
        // Set before CLR startup; the worker cannot allocate an unbounded managed heap before producing a command.
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000";
        start.Environment["COMPlus_GCHeapHardLimit"] = "0x4000000";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Lua worker.");
        using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
        try
        {
            // The child waits on stdin: OS memory limits are installed before any untrusted source is sent.
            using var job = LuaWorkerJob.Attach(process);
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumResponseBytes, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError.BaseStream, 8192, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var bytes = await output.ConfigureAwait(false);
            var errorBytes = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) return new($"Изолированный Lua завершён аварийно (код {process.ExitCode}); превышение ресурсов или ошибка парсера. Команды отклонены. " + Encoding.UTF8.GetString(errorBytes), []);
            return JsonSerializer.Deserialize<LuaWorkerResponse>(bytes) ?? new("Пустой ответ Lua worker.", []);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
        }
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

    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
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
