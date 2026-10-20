using System.Diagnostics;
using System.Text;

namespace NovellMaker.Core.Execution;

/// <summary>Only the process/pipe boundary is replaceable in transport tests; Lua stays out of the host.</summary>
internal interface ILuaWorkerProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    int ExitCode { get; }
    void ApplyLimits();
    void CompleteInput();
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class LuaWorkerProcess : ILuaWorkerProcess
{
    private readonly Process process;
    private LuaWorkerJob? job;

    internal LuaWorkerProcess(string host)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        start.ArgumentList.Add("--lua-worker");
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000";
        start.Environment["COMPlus_GCHeapHardLimit"] = "0x4000000";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        process = Process.Start(start) ?? throw new IOException("Не удалось запустить Lua worker.");
    }

    public Stream Input => process.StandardInput.BaseStream;
    internal int Id => process.Id;
    public Stream Output => process.StandardOutput.BaseStream;
    public Stream Error => process.StandardError.BaseStream;
    public int ExitCode => process.ExitCode;
    public void ApplyLimits() => job = LuaWorkerJob.Attach(process);
    public void CompleteInput() => process.StandardInput.Close();
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public void Kill()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    public void Dispose()
    {
        // Closing the job also kills remaining children on Windows, including on cleanup failure.
        job?.Dispose();
        process.Dispose();
    }
}
