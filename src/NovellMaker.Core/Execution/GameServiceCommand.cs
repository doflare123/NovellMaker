using System.Collections.Immutable;

namespace NovellMaker.Core.Execution;

public enum GameServiceAction { NewGame, MainMenu, EndSession, ContinueGame, ListSlots, SaveSlot, LoadSlot, DeleteSlot }
public sealed record GameServiceCommand(Guid RequestId, GameServiceAction Action, string? SlotId = null, string? Title = null, Guid? SceneId = null)
{
    public void Validate()
    {
        if (RequestId == Guid.Empty || !Enum.IsDefined(Action) ||
            (Action is GameServiceAction.SaveSlot or GameServiceAction.LoadSlot or GameServiceAction.DeleteSlot
                ? SlotId is not null && !GameSlotStore.IsValidSlotId(SlotId) || Action != GameServiceAction.SaveSlot && SlotId is null
                : SlotId is not null) ||
            (Action == GameServiceAction.SaveSlot ? Title is { Length: > 128 } : Title is not null) ||
            (Action == GameServiceAction.NewGame ? SceneId == Guid.Empty : SceneId is not null))
            throw new ArgumentException("Некорректная команда игрового сервиса.");
    }
}

public sealed record GameServiceResult(Guid RequestId, GameServiceAction Action, bool Success, string Message,
    string? SlotId, ImmutableArray<GameSlotInfo> Slots)
{
    public void Validate()
    {
        if (RequestId == Guid.Empty || !Enum.IsDefined(Action) || Message is null || Message.Length > 1024 ||
            SlotId is not null && !GameSlotStore.IsValidSlotId(SlotId) || Slots.IsDefault || Slots.Length > GameSlotStore.MaxSlots ||
            Action != GameServiceAction.ListSlots && !Slots.IsEmpty || !Success && !Slots.IsEmpty ||
            Slots.Any(s => s is null || !GameSlotStore.IsValidSlotId(s.Id) || !Enum.IsDefined(s.Status) || s.Diagnostic?.Length > 1024 ||
                s.Status == GameSlotStatus.Compatible && s.Metadata is null || s.Metadata is { IsValid: false }) ||
            Slots.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Slots.Length)
            throw new ArgumentException("Некорректный результат игрового сервиса.");
    }
}
