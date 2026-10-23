using System.Diagnostics;
using NovellMaker.Core.Projects;

namespace NovellMaker.Editor.Services.Preview;

/// <summary>Launches the shared game host from an immutable authoring snapshot.</summary>
public sealed class PreviewService : IPreviewService
{
    private Process? process;
    private bool disposed;
    private readonly string runtime;
    private readonly string temporaryRoot;
    private readonly bool hidden;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Process, byte> stopped = new();
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal int? ActiveProcessId
    {
        get { try { return process?.Id; } catch (InvalidOperationException) { return null; } }
    }

    public PreviewService() : this(Path.Combine(AppContext.BaseDirectory, "Runtime", OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime"),
        Path.Combine(Path.GetTempPath(), "NovellMaker.Preview")) { }

    internal PreviewService(string runtime, string temporaryRoot, bool hidden = false)
    {
        this.runtime = runtime;
        this.temporaryRoot = temporaryRoot;
        this.hidden = hidden;
    }

    public async Task StartAsync(PreviewRequest request, Action<string> report)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        DialogueDisplayValidation.Validate(request.Project);
        await Task.Run(() => NovellMaker.Core.Scripting.LuaModuleLibrary.ValidateProject(request.Project));
        ObjectDisposedException.ThrowIf(disposed, this);
        StopOwnedProcess();
        if (!File.Exists(runtime)) throw new FileNotFoundException("Игровой модуль отсутствует. Соберите редактор вместе с NovellMaker.Runtime.", runtime);
        var directory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshotPath = Path.Combine(directory, "snapshot.novell");
        Process? started = null;
        try
        {
            await new JsonProjectStore().SaveAsync(snapshotPath, request.Project);
            ObjectDisposedException.ThrowIf(disposed, this);
            var start = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8 };
            if (hidden && OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
            foreach (var argument in new[] { "--project", snapshotPath, "--assets-root", request.AssetsRoot, "--scene", request.SceneId.ToString(), "--language", request.Language }) start.ArgumentList.Add(argument);
            if (request.LineId is { } line) { start.ArgumentList.Add("--line"); start.ArgumentList.Add(line.ToString()); }
            started = Process.Start(start) ?? throw new IOException("Не удалось запустить игровой предпросмотр.");
            process = started;
            var context = SynchronizationContext.Current;
            void Report(string message)
            {
                if (disposed) return;
                if (context is null) report(message);
                else context.Post(_ => { if (!disposed) report(message); }, null);
            }
            var completion = ObserveAsync(started, directory, Report);
            Completion = completion;
            // Surface immediate native/backend startup failures through the normal editor error dialog.
            if (await Task.WhenAny(completion, Task.Delay(350)) == completion)
            {
                var error = await completion;
                if (error is not null) throw new IOException(error);
            }
        }
        catch
        {
            if (started is null) Cleanup(directory);
            throw;
        }
    }

    private async Task<string?> ObserveAsync(Process child, string directory, Action<string> report)
    {
        try
        {
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            var details = await stderr;
            if (stopped.TryRemove(child, out _)) return null;
            var error = child.ExitCode == 0 ? null : $"Предпросмотр завершился с кодом {child.ExitCode}: {details[..Math.Min(details.Length, 8192)]}";
            report(error ?? "Окно игрового предпросмотра закрыто.");
            return error;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            report($"Ошибка игрового предпросмотра: {error.Message}");
            return error.Message;
        }
        finally
        {
            if (ReferenceEquals(process, child)) process = null;
            child.Dispose();
            Cleanup(directory);
        }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            File.Delete(Path.Combine(directory, "snapshot.novell"));
            Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void StopOwnedProcess()
    {
        var child = process;
        process = null;
        try
        {
            if (child is { HasExited: false })
            {
                stopped.TryAdd(child, 0);
                child.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopOwnedProcess();
    }
}
