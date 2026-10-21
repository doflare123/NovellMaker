namespace NovellMaker.Core.Screens.Lua;

/// <summary>Whitelisted properties; absent values preserve the existing value.</summary>
public sealed record ScreenNodePatch
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? AnchorX { get; init; }
    public double? AnchorY { get; init; }
    public double? PivotX { get; init; }
    public double? PivotY { get; init; }
    public double? ScaleX { get; init; }
    public double? ScaleY { get; init; }
    public double? Rotation { get; init; }
    public double? Opacity { get; init; }
    public string? Color { get; init; }
    public int? Order { get; init; }
    public bool? ClipChildren { get; init; }

    internal ScreenNode Apply(ScreenNode node) => node with
    {
        Bounds = new(X ?? node.Bounds.X, Y ?? node.Bounds.Y, Width ?? node.Bounds.Width, Height ?? node.Bounds.Height),
        Geometry = node.Geometry with { AnchorX = AnchorX ?? node.Geometry.AnchorX, AnchorY = AnchorY ?? node.Geometry.AnchorY,
            PivotX = PivotX ?? node.Geometry.PivotX, PivotY = PivotY ?? node.Geometry.PivotY,
            ScaleX = ScaleX ?? node.Geometry.ScaleX, ScaleY = ScaleY ?? node.Geometry.ScaleY,
            Rotation = Rotation ?? node.Geometry.Rotation, Opacity = Opacity ?? node.Geometry.Opacity },
        Color = Color ?? node.Color, Order = Order ?? node.Order, ClipChildren = ClipChildren ?? node.ClipChildren
    };
}

/// <summary>Wire-independent live identity. A resource/template GUID alone grants no live access.</summary>
public readonly record struct ScreenNodeHandle(Guid ResourceId, Guid InstanceId, Guid NodeId)
{
    public override string ToString() => $"{ResourceId:N}/{InstanceId:N}/{NodeId:N}";
    public static bool TryParse(string value, out ScreenNodeHandle handle)
    {
        handle = default; var parts = value.Split('/');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var resource) || !Guid.TryParseExact(parts[1], "N", out var instance) ||
            !Guid.TryParseExact(parts[2], "N", out var node) || resource == Guid.Empty || instance == Guid.Empty || node == Guid.Empty) return false;
        handle = new(resource, instance, node); return true;
    }
}
