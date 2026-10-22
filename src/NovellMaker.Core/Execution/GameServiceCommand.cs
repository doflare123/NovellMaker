using System.Collections.Immutable;
using NovellMaker.Core.Profiles;

namespace NovellMaker.Core.Execution;

public enum GameServiceAction { NewGame, MainMenu, EndSession, ContinueGame, ListSlots, SaveSlot, LoadSlot, DeleteSlot, GetSettings, SetSetting }
public sealed record GameServiceCommand(Guid RequestId, GameServiceAction Action, string? SlotId = null, string? Title = null, Guid? SceneId = null)
{
    public PlayerSettingChange? Setting { get; init; }
    public void Validate()
    {
        if (RequestId == Guid.Empty || !Enum.IsDefined(Action) ||
            (Action is GameServiceAction.SaveSlot or GameServiceAction.LoadSlot or GameServiceAction.DeleteSlot
                ? SlotId is not null && !GameSlotStore.IsValidSlotId(SlotId) || Action != GameServiceAction.SaveSlot && SlotId is null
                : SlotId is not null) ||
            (Action == GameServiceAction.SaveSlot ? Title is { Length: > 128 } : Title is not null) ||
            (Action == GameServiceAction.NewGame ? SceneId == Guid.Empty : SceneId is not null) ||
            (Action == GameServiceAction.SetSetting ? Setting is null : Setting is not null))
            throw new ArgumentException("Некорректная команда игрового сервиса.");
        Setting?.Validate();
    }
}

public sealed record GameServiceResult(Guid RequestId, GameServiceAction Action, bool Success, string Message,
    string? SlotId, ImmutableArray<GameSlotInfo> Slots)
{
    public PlayerSettings? Settings { get; init; }
    public void Validate()
    {
        if (RequestId == Guid.Empty || !Enum.IsDefined(Action) || Message is null || Message.Length > 1024 ||
            SlotId is not null && !GameSlotStore.IsValidSlotId(SlotId) || Slots.IsDefault || Slots.Length > GameSlotStore.MaxSlots ||
            Action != GameServiceAction.ListSlots && !Slots.IsEmpty || !Success && !Slots.IsEmpty ||
            Slots.Any(s => s is null || !GameSlotStore.IsValidSlotId(s.Id) || !Enum.IsDefined(s.Status) || s.Diagnostic?.Length > 1024 ||
                s.Status == GameSlotStatus.Compatible && s.Metadata is null || s.Metadata is { IsValid: false }) ||
            Slots.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Slots.Length ||
            (Action is GameServiceAction.GetSettings or GameServiceAction.SetSetting ? Success && Settings is null : Settings is not null))
            throw new ArgumentException("Некорректный результат игрового сервиса.");
        Settings?.Validate();
    }
}
