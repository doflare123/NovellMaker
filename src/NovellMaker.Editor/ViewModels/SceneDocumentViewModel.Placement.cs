using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class SceneDocumentViewModel
{
    private Guid? placementCharacterId;
    public IReadOnlyList<CharacterOption> PlacementOptions => SpeakerOptions.Where(option => option.Id.HasValue).ToArray();
    public CharacterOption? SelectedPlacementCharacter
    {
        get => PlacementOptions.FirstOrDefault(option => option.Id == placementCharacterId);
        set
        {
            if (value is null || value.Id == placementCharacterId) return;
            Session.EndEditGroup();
            placementCharacterId = value.Id;
            RefreshPlacement();
        }
    }

    public bool CanPlaceCharacter => CanEditLine && SelectedPlacementCharacter is not null;
    private CharacterPlacement? EffectivePlacement => placementCharacterId is { } id ? SampleFrame?.Placements.GetValueOrDefault(id) : null;
    private CharacterPlacement CurrentPlacement
    {
        get
        {
            if (EffectivePlacement is { } placement) return placement;
            var sprites = SampleFrame?.Characters ?? [];
            var index = Array.FindIndex(sprites.ToArray(), sprite => sprite.CharacterId == placementCharacterId);
            var x = index < 0 ? 0.5 : (40 + 1200d / sprites.Length * (sprites[index].AutomaticSlot + 0.5)) / CharacterLayout.Width;
            var autoScale = 1d;
            if (index >= 0)
            {
                var image = sprites[index].Image;
                var automatic = CharacterLayout.Calculate(image.PixelWidth, image.PixelHeight, sprites[index].AutomaticSlot, sprites.Length, null);
                var manual = CharacterLayout.Calculate(image.PixelWidth, image.PixelHeight, sprites[index].AutomaticSlot, sprites.Length, new(0.5, 1, 1, 0));
                autoScale = Math.Clamp(automatic.Width / manual.Width, 0.05, 5);
            }
            return new(x, 1, autoScale, 0);
        }
    }

    public decimal PlacementX { get => (decimal)(CurrentPlacement.X * 100); set { if (value != PlacementX) SetPlacement(CurrentPlacement with { X = (double)value / 100 }, "x"); } }
    public decimal PlacementY { get => (decimal)(CurrentPlacement.Y * 100); set { if (value != PlacementY) SetPlacement(CurrentPlacement with { Y = (double)value / 100 }, "y"); } }
    public decimal PlacementScale { get => (decimal)(CurrentPlacement.Scale * 100); set { if (value != PlacementScale) SetPlacement(CurrentPlacement with { Scale = (double)value / 100 }, "scale"); } }
    public decimal PlacementLayer { get => CurrentPlacement.Layer; set { if (value is >= -1000 and <= 1000 && value == decimal.Truncate(value) && value != PlacementLayer) SetPlacement(CurrentPlacement with { Layer = (int)value }, "layer"); } }
    public string PlacementStatus => SelectedLine?.Model.Placements.Any(command => command.CharacterId == placementCharacterId) == true
        ? "Команда на этой реплике" : EffectivePlacement is null ? "Автоматическое размещение" : "Размещение из предыдущих реплик";

    public void SetPlacement(CharacterPlacement? placement, string? group = null)
    {
        if (!CanPlaceCharacter || placementCharacterId is not { } id || SelectedLine is not { } line || placement is { IsValid: false }) return;
        if (line.Model.Placements.Any(command => command.CharacterId == id && command.Placement == placement)) return;
        var commands = line.Model.Placements.Where(command => command.CharacterId != id).ToImmutableArray()
            .Add(new(id, placement));
        Session.UpdateScene(scene with { Lines = scene.Lines.SetItem(SelectedIndex, line.Model with { Placements = commands }) },
            group is null ? null : $"placement:{line.Id}:{id}:{group}");
    }

    public Task ConfirmRemovePlacementAsync()
    {
        if (!CanPlaceCharacter || SelectedLine is not { } line || placementCharacterId is not { } character ||
            !line.Model.Placements.Any(p => p.CharacterId == character)) return Task.CompletedTask;
        var lineId = line.Id;
        return Session.ConfirmEditorDeletionAsync("Команда размещения", () =>
        {
            var index = Array.FindIndex(scene.Lines.ToArray(), l => l.Id == lineId);
            if (index >= 0) Session.UpdateScene(scene with { Lines = scene.Lines.SetItem(index,
                scene.Lines[index] with { Placements = scene.Lines[index].Placements.Where(p => p.CharacterId != character).ToImmutableArray() }) });
        });
    }

    public void RemovePlacementCommand()
    {
        if (!CanPlaceCharacter || SelectedLine is not { } line) return;
        ChangeLine(line.Model with { Placements = line.Model.Placements.Where(command => command.CharacterId != placementCharacterId).ToImmutableArray() });
    }

    private void RefreshPlacement()
    {
        if (!PlacementOptions.Any(option => option.Id == placementCharacterId)) placementCharacterId = PlacementOptions.FirstOrDefault()?.Id;
        foreach (var property in new[] { nameof(PlacementOptions), nameof(SelectedPlacementCharacter), nameof(CanPlaceCharacter),
                     nameof(PlacementX), nameof(PlacementY), nameof(PlacementScale), nameof(PlacementLayer), nameof(PlacementStatus) }) Changed(property);
    }
}
