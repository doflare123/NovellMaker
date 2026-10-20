namespace NovellMaker.Core.Execution;

/// <summary>Cooperative cancellation plus the transport's monotonic deadline, including CPU-bound preparation.</summary>
internal readonly record struct LuaRequestPreparation(CancellationToken CancellationToken, Action Checkpoint)
{
    internal static LuaRequestPreparation ForCancellation(CancellationToken token) => new(token, token.ThrowIfCancellationRequested);
}
