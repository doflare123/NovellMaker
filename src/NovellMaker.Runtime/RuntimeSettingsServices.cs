using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

/// <summary>Profile writes outlive gameplay/screens. Only callbacks belong to the requesting lifetime.</summary>
internal sealed class RuntimeSettingsServices(GameSession session, PlayerSettingsSession settings, Action<PlayerSettings> apply)
{
    private sealed record Pending(ScreenInstance? Origin, GameServiceCommand Command, long Generation, long Revision);
    private Pending? pending;
    internal Func<ScreenInstance, bool>? IsCurrentOrigin { get; set; }
    internal event Action<ScreenInstance?, GameServiceResult>? Completed;
    internal GameServiceResult? LastResult { get; private set; }
    private bool IsCurrent(ScreenInstance? origin, long generation) => session.IsCurrentGeneration(generation) &&
        (origin is null || IsCurrentOrigin?.Invoke(origin) != false);

    internal bool Request(ScreenInstance? origin, GameServiceCommand command, long generation)
    {
        command.Validate();
        if (command.Action is not (GameServiceAction.GetSettings or GameServiceAction.SetSetting or GameServiceAction.GetPersistent or GameServiceAction.SetPersistent))
            throw new ArgumentException("Ожидалась команда профиля.");
        if (!IsCurrent(origin, generation)) return false;
        if (command.Action is GameServiceAction.GetSettings or GameServiceAction.GetPersistent)
        { Complete(origin, command, true, settings.Diagnostic); return true; }
        if (pending is { } previous && !IsCurrent(previous.Origin, previous.Generation)) pending = null;
        if (pending is not null)
        { Complete(origin, command, false, "Запись профиля уже выполняется."); return false; }
        if (command.Action == GameServiceAction.SetPersistent)
        {
            long revision;
            try { revision = settings.SetPersistent(new(command.Key!, command.DataValue)); }
            catch (ArgumentException)
            { Complete(origin, command, false, "Превышен бюджет постоянных данных профиля."); return false; }
            if (!settings.IsBusy) { Complete(origin, command, true, "Постоянные данные не изменились."); return true; }
            pending = new(origin, command, generation, revision); LastResult = null; return true;
        }
        var updated = settings.Current.Apply(command.Setting!);
        try { apply(updated); }
        catch (InvalidOperationException)
        { Complete(origin, command, false, "Не удалось применить режим окна. Настройки не изменены."); return false; }
        pending = new(origin, command, generation, settings.Set(updated));
        LastResult = null;
        return true;
    }

    internal bool Poll()
    {
        var changed = settings.Poll();
        if (pending is { } work)
        {
            if (!IsCurrent(work.Origin, work.Generation)) pending = null;
            else if (settings.LastWrite is { } write && write.Revision >= work.Revision)
            { pending = null; Complete(work.Origin, work.Command, write.Success, write.Message); }
        }
        return changed;
    }

    private void Complete(ScreenInstance? origin, GameServiceCommand command, bool success, string message)
    {
        var data = command.Action is GameServiceAction.GetPersistent or GameServiceAction.SetPersistent;
        var result = new GameServiceResult(command.RequestId, command.Action, success, message, null, [])
        {
            Settings = data ? null : settings.Current,
            ProfileSaved = settings.IsPersisted,
            Key = command.Key,
            DataValue = data ? settings.GetPersistent(command.Key!) : null,
        };
        result.Validate(); LastResult = result; Completed?.Invoke(origin, result);
    }
}
