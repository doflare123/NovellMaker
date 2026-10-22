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
        if (command.Action is not (GameServiceAction.GetSettings or GameServiceAction.SetSetting)) throw new ArgumentException("Ожидалась команда настроек.");
        if (!IsCurrent(origin, generation)) return false;
        if (command.Action == GameServiceAction.GetSettings)
        { Complete(origin, command, true, settings.Diagnostic); return true; }
        if (pending is { } previous && !IsCurrent(previous.Origin, previous.Generation)) pending = null;
        if (pending is not null)
        { Complete(origin, command, false, "Запись настроек уже выполняется."); return false; }
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
        var result = new GameServiceResult(command.RequestId, command.Action, success, message, null, []) { Settings = settings.Current };
        result.Validate(); LastResult = result; Completed?.Invoke(origin, result);
    }
}
