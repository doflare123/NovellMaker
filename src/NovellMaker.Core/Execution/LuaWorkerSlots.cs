namespace NovellMaker.Core.Execution;

// A persistent screen worker reserves at most one slot; v1 can still execute beside it.
internal static class LuaWorkerSlots
{
    internal static readonly SemaphoreSlim Processes = new(LuaWorkerClient.MaximumWorkers);
    internal static readonly SemaphoreSlim Requests = new(LuaWorkerClient.MaximumWorkers + LuaWorkerClient.MaximumPendingRequests);
    internal static readonly SemaphoreSlim ScreenSessions = new(1);
}
