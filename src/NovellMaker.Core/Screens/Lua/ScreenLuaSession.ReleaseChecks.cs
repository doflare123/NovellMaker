using NovellMaker.Core.Execution;
using NovellMaker.Core.ExternalServices;

namespace NovellMaker.Core.Screens.Lua;

public sealed partial class ScreenLuaSession
{
    private ReleaseCheckService? releaseChecks;
    private Func<System.Collections.Immutable.ImmutableDictionary<string, string>>? releaseSnapshot;

    /// <summary>Profile-scoped host service. Reset closes owners while retaining its cache/cooldown.</summary>
    public void ConnectReleaseChecks(ReleaseCheckService service,
        Func<System.Collections.Immutable.ImmutableDictionary<string, string>>? snapshot = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(service);
        if (ReferenceEquals(service, releaseChecks)) return;
        if (entries.Values.Any(e => e.ReleaseOwner is not null))
            throw new InvalidOperationException("Нельзя заменить сервис при живых владельцах проверки выпуска.");
        releaseChecks = service;
        releaseSnapshot = snapshot;
    }

    public bool HasReleaseChecks(ScreenInstance instance) => IsAvailable(instance) &&
        entries[instance.Id].ReleaseOwner is { } owner &&
        releaseChecks!.IsCurrentOwner(owner) &&
        (releaseChecks!.IsBusy(owner) || entries[instance.Id].QueuedReleases.Count != 0 ||
         serviceReplies.Values.Any(r => ReferenceEquals(r.Entry.Instance, instance) && r.Action == GameServiceAction.CheckGameRelease));

    private void ValidateReleaseCheck(Work work, GameServiceCommand? command)
    {
        if (command?.Action is not (GameServiceAction.CheckGameRelease or GameServiceAction.CancelReleaseCheck)) return;
        if (releaseChecks is null) throw new InvalidOperationException("Сервис проверки выпуска не подключён.");
        var entry = work.Entry;
        try
        {
            entry.ReleaseOwner ??= releaseChecks.Attach(entry.Instance.Id, Generation);
            if (command.Action == GameServiceAction.CheckGameRelease)
                releaseChecks.ValidateCheck(entry.ReleaseOwner, command.RequestId, work.ChainDepth, work.Origin);
            else releaseChecks.ValidateCancel(entry.ReleaseOwner, command.ReleaseRequestId!.Value, work.ChainDepth);
        }
        catch (ArgumentException error) { throw new InvalidOperationException(error.Message, error); }
    }

    private bool CommitReleaseCheck(Work work, GameServiceCommand command)
    {
        if (command.Action == GameServiceAction.CheckGameRelease)
            releaseChecks!.Check(work.Entry.ReleaseOwner!, work.ChainDepth, work.Origin, command.RequestId);
        else if (command.Action == GameServiceAction.CancelReleaseCheck)
            releaseChecks!.Cancel(work.Entry.ReleaseOwner!, command.ReleaseRequestId!.Value, work.ChainDepth);
        else return false;
        return true;
    }

    private void PollReleaseChecks()
    {
        releaseChecks?.Poll(); // Deadline/cache publication continue while callback delivery is paused.
        foreach (var entry in entries.Values.ToArray())
        {
            if (!IsAvailable(entry.Instance) || entry.ReleaseOwner is not { } owner) continue;
            if (owner.Generation != Generation || !releaseChecks!.IsCurrentOwner(owner))
            {
                Detach(entry);
                Report("Владелец проверки выпуска больше не действует; ответ отброшен.", ScreenLuaIssueKind.StaleEvent, entry.Instance.Id);
                continue;
            }
            if (entry.SuspendedAt is not null) continue;
            foreach (var result in releaseChecks!.PendingResults(owner))
            {
                if (entry.QueuedReleases.Contains(result.RequestId)) continue;
                if (!serviceReplies.TryGetValue(result.RequestId, out var reply) || !ReferenceEquals(reply.Entry, entry)) continue;
                serviceReplies[result.RequestId] = reply with { ChainDepth = result.Depth };
                var value = new GameServiceResult(result.RequestId, GameServiceAction.CheckGameRelease, result.Success, result.Message, null, []) { ReleaseCheck = result };
                if (EnqueueCore(entry.Instance, new("service_result") { Result = value }, releaseSnapshot?.Invoke() ?? entry.Snapshot, true) == ScreenEventAdmission.Accepted)
                    entry.QueuedReleases.Add(result.RequestId);
            }
        }
    }

    private void AcknowledgeReleaseCheck(Work work)
    {
        if (work.Event?.Result?.ReleaseCheck is not { } result) return;
        if (!releaseChecks!.TryTakeResult(work.Entry.ReleaseOwner!, out var taken) || taken!.RequestId != result.RequestId)
            throw new InvalidOperationException("Нарушен порядок результатов проверки выпуска.");
        work.Entry.QueuedReleases.Remove(result.RequestId);
    }

    private void ClearReleaseChecks(Entry entry)
    {
        if (entry.ReleaseOwner is { } owner) releaseChecks?.Close(owner);
        entry.ReleaseOwner = null;
        entry.QueuedReleases.Clear();
    }
}
