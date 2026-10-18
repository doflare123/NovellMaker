namespace NovellMaker.Core.Scenes;

/// <summary>Bottom-center anchor in scene fractions; scale relative to a 360 × 560 fit.</summary>
public sealed record CharacterPlacement(double X, double Y, double Scale, int Layer)
{
    public bool IsValid => double.IsFinite(X) && X is >= -1 and <= 2 &&
        double.IsFinite(Y) && Y is >= -1 and <= 2 &&
        double.IsFinite(Scale) && Scale is >= 0.05 and <= 5 && Layer is >= -1000 and <= 1000;
}

/// <summary>Null placement explicitly restores automatic placement for this character.</summary>
public sealed record CharacterPlacementCommand(Guid CharacterId, CharacterPlacement? Placement);
