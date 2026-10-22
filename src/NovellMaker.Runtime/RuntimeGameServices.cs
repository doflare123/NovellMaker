using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

/// <summary>Host-thread bridge shared by project bindings and screen Lua. Never awaits I/O.</summary>
internal sealed class RuntimeGameServices : IDisposable
{
    private readonly GameSession session;
    private readonly GameSessionSlots slots;
    private sealed record Pending(ScreenInstance? Origin, GameServiceCommand Command, long Generation);
    private Pending? pending;
    private bool disposed;
    internal GameServiceResult? LastResult { get; private set; }
    internal Func<ScreenInstance, bool>? IsCurrentOrigin { get; set; }
    internal event Action<ScreenInstance?, GameServiceResult>? Completed;

    internal RuntimeGameServices(GameSession session, GameSessionSlots slots)
    { this.session = session; this.slots = slots; session.Resetting += Reset; }

    internal bool Request(ScreenInstance? origin, GameServiceCommand command, long generation)
    {
        command.Validate();
        if (disposed || !session.IsCurrentGeneration(generation) || origin is not null && IsCurrentOrigin?.Invoke(origin) == false) return false;
        if (command.Action is not (GameServiceAction.NewGame or GameServiceAction.MainMenu or GameServiceAction.EndSession) && (slots.IsBusy || pending is not null))
        { Complete(new(origin, command, generation), false, "Операция слотов уже выполняется."); return false; }
        LastResult = null;
        bool success;
        switch (command.Action)
        {
            case GameServiceAction.NewGame: success = session.NewGame(generation, command.SceneId); break;
            case GameServiceAction.MainMenu: success = session.ReturnToMenu(generation); break;
            case GameServiceAction.EndSession: success = session.End(generation); break;
            default:
                if (command.Action == GameServiceAction.SaveSlot && command.SlotId is null)
                    command = command with { SlotId = "slot-" + Guid.NewGuid().ToString("N") };
                var work = new Pending(origin, command, generation);
                pending = work;
                success = command.Action switch
                {
                    GameServiceAction.ContinueGame => slots.Continue(),
                    GameServiceAction.ListSlots => slots.List(),
                    GameServiceAction.SaveSlot => slots.Save(command.SlotId!, command.Title),
                    GameServiceAction.LoadSlot => slots.Load(command.SlotId!),
                    GameServiceAction.DeleteSlot => slots.Delete(command.SlotId!),
                    _ => false,
                };
                if (!success) { pending = null; Complete(work, false, slots.LastResult?.Message ?? "Операция недоступна."); }
                return success;
        }
        // A successful transition owns a new lifetime; the old screen must receive no callback.
        if (!success) Complete(new(origin, command, generation), false, "Команда недоступна в текущем сеансе.");
        return success;
    }

    internal bool Poll()
    {
        var work = pending;
        if (work?.Origin is { } origin && IsCurrentOrigin?.Invoke(origin) == false)
        { pending = null; slots.Cancel(); LastResult = null; return false; }
        if (!slots.Poll() || slots.LastResult is not { } result) return false;
        pending = null;
        if (work is not null && session.IsCurrentGeneration(work.Generation))
            Complete(work, result.Success, result.Message, result.Slots.IsDefault ? [] : result.Slots);
        // Successful load has reset all origins. Preserve only window feedback, never deliver into a new handler.
        else if (work is not null && result.Success)
            LastResult = new(work.Command.RequestId, work.Command.Action, true, result.Message, work.Command.SlotId, []);
        return true;
    }

    private void Complete(Pending work, bool success, string message, System.Collections.Immutable.ImmutableArray<GameSlotInfo> items = default)
    {
        var result = new GameServiceResult(work.Command.RequestId, work.Command.Action, success,
            message.Length <= 1024 ? message : message[..1024], work.Command.SlotId,
            items.IsDefault ? [] : items.Select(item => item.Status == GameSlotStatus.Unavailable
                ? item with { Diagnostic = "Файл слота недоступен." } : item).ToImmutableArray());
        result.Validate(); LastResult = result; Completed?.Invoke(work.Origin, result);
    }
    private void Reset() { pending = null; LastResult = null; }
    public void Dispose() { if (disposed) return; disposed = true; session.Resetting -= Reset; Reset(); }
}
