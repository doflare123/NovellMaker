using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NovellMaker.Core.Execution;

/// <summary>Windows per-process committed-memory cap, independent of CLR allocation accounting.</summary>
internal sealed class LuaWorkerJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private LuaWorkerJob() : base(true) { }
    public static LuaWorkerJob? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null; // Managed heap hard limit and parent timeout still apply.
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) { job.Dispose(); throw new IOException("Не удалось создать ограничение ресурсов Lua."); }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x100 | 0x2000 }, ProcessMemoryLimit = 256 * 1024 * 1024 };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
        { job.Dispose(); throw new IOException("Не удалось применить лимит памяти Lua. Исполнение запрещено."); }
        return job;
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern LuaWorkerJob CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(LuaWorkerJob job, int kind, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(LuaWorkerJob job, nint process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
