using System.Collections.Immutable;

namespace NovellMaker.Core.Screens;

public enum ScreenNodeKind { Container, Panel, Text, Button, Image, Area }

/// <summary>Logical pixels; child rectangles are relative to their parent anchor.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Math.Abs(X) <= 100000 && Math.Abs(Y) <= 100000 &&
        Width > 0 && Width <= 100000 && Height > 0 && Height <= 100000;
}

public sealed record ScreenNode(Guid Id, Guid? ParentId, ScreenNodeKind Kind, ScreenRect Bounds,
    string Text = "", bool Visible = true, bool Enabled = true)
{
    public Guid? ImageId { get; init; }
    public string Name { get; init; } = "";
    [System.Text.Json.Serialization.JsonRequired] public ScreenGeometry Geometry { get; init; } = new();
    [System.Text.Json.Serialization.JsonRequired] public bool ClipChildren { get; init; } = true;
    [System.Text.Json.Serialization.JsonRequired] public int Order { get; init; }
    [System.Text.Json.Serialization.JsonRequired] public string Color { get; init; } = "#FFFFFF";
}

/// <summary>Immutable, validated screen resource, independent of graphics and script execution.</summary>
public sealed class ScreenDefinition
{
    public static ScreenRect CanvasViewport => new(0, 0, 1280, 720);
    public const int MaxNodes = 1024;
    public const int MaxDepth = 32;
    public const int MaxTextCharacters = 131072;
    public const int MaxNodeIdentities = 4096;
    private readonly ImmutableDictionary<Guid, ImmutableArray<ScreenNode>> children;
    private readonly ScreenNode root;
    private readonly ImmutableArray<ScreenVisual> visuals;
    private readonly ImmutableDictionary<Guid, ScreenVisual> layout;

    public ScreenDefinition(Guid id, ImmutableArray<ScreenNode> nodes) : this(id, nodes, []) { }

    internal ScreenDefinition(Guid id, ImmutableArray<ScreenNode> nodes, ImmutableHashSet<Guid> retired)
    {
        if (id == Guid.Empty || nodes.IsDefaultOrEmpty || nodes.Length > MaxNodes || nodes.Length + retired.Count > MaxNodeIdentities || nodes.Any(n => n is not null && retired.Contains(n.Id)))
            throw new ArgumentException("Экран должен иметь ID и от 1 до 1024 узлов.");
        var map = new Dictionary<Guid, ScreenNode>();
        var characters = 0;
        foreach (var node in nodes)
        {
            if (node is null || node.Id == Guid.Empty || !map.TryAdd(node.Id, node) ||
                !Enum.IsDefined(node.Kind) || node.ImageId == Guid.Empty || node.Name is null || node.Name.Length > 128 || !node.Bounds.IsValid || node.Text is null || node.Text.Length > 8192 ||
                node.Geometry is null || !node.Geometry.IsValid || !ScreenTint.TryParse(node.Color, out _) || Math.Abs((long)node.Order) > 100000)
                throw new ArgumentException("Недопустимый узел экрана.");
            characters += node.Text.Length;
        }
        if (characters > MaxTextCharacters || nodes.Count(n => n.ParentId is null) != 1)
            throw new ArgumentException("Экран должен иметь один корень и ограниченный объём текста.");
        foreach (var node in nodes)
        {
            var current = node;
            var depth = 1;
            while (current.ParentId is { } parent)
            {
                if (++depth > MaxDepth || !map.TryGetValue(parent, out var ancestor) ||
                    ancestor.Kind is not (ScreenNodeKind.Container or ScreenNodeKind.Panel))
                    throw new ArgumentException("Неверный родитель, цикл или глубина дерева экрана.");
                current = ancestor;
            }
        }
        Id = id;
        RetiredNodeIds = retired;
        Nodes = nodes;
        root = nodes.Single(n => n.ParentId is null);
        children = nodes.Where(n => n.ParentId is not null).GroupBy(n => n.ParentId!.Value)
            .ToImmutableDictionary(g => g.Key, g => g.OrderBy(n => n.Order).ToImmutableArray());
        var prepared = ImmutableArray.CreateBuilder<ScreenVisual>();
        Build(root, ScreenMatrix.Identity, CanvasViewport.Width, CanvasViewport.Height, null, true, true, 1, ScreenTint.White);
        visuals = prepared.ToImmutable();
        layout = visuals.ToImmutableDictionary(v => v.Node.Id);
        Buttons = [.. Visuals(CanvasViewport).Where(v => v.Node.Kind is ScreenNodeKind.Button or ScreenNodeKind.Area && v.Enabled).Select(v => v.Node)];

        void Build(ScreenNode node, ScreenMatrix parent, double width, double height,
            ImmutableArray<ScreenPoint>? clip, bool visible, bool enabled, double opacity, ScreenTint tint)
        {
            var world = node.Geometry.Matrix(node.Bounds, width, height).Then(parent);
            var inverse = world.Inverse();
            var rectangle = ScreenPolygon.Transform(ScreenPolygon.Rectangle(new(0, 0, node.Bounds.Width, node.Bounds.Height)), world);
            if (rectangle.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1e8 || Math.Abs(p.Y) > 1e8))
                throw new ArgumentException("Превышен диапазон мировой геометрии узла.");
            visible &= node.Visible; enabled &= node.Enabled; opacity *= node.Geometry.Opacity;
            ScreenTint.TryParse(node.Color, out var color); tint = tint.Multiply(color);
            var polygon = clip is { } boundary ? ScreenPolygon.Clip(rectangle, boundary) : rectangle;
            prepared.Add(new(node, world, inverse, visible && opacity > 0 ? polygon : [], opacity, tint, enabled));
            var childClip = node.ClipChildren ? polygon : clip;
            foreach (var child in children.GetValueOrDefault(node.Id, []))
                Build(child, world, node.Bounds.Width, node.Bounds.Height, childClip, visible, enabled, opacity, tint);
        }
    }

    public Guid Id { get; }
    internal ImmutableHashSet<Guid> RetiredNodeIds { get; }
    /// <summary>Sibling order is paint order; later sibling subtrees are above earlier ones.</summary>
    public ImmutableArray<ScreenNode> Nodes { get; }

    public ImmutableArray<ScreenNode> Buttons { get; }

    public IEnumerable<ScreenVisual> Visuals(ScreenRect viewport) => visuals
        .Select(v => v with { Polygon = ScreenPolygon.Clip(v.Polygon, ScreenPolygon.Rectangle(viewport)) })
        .Where(v => !v.Polygon.IsEmpty);

    public IEnumerable<(ScreenNode Node, ScreenRect Bounds)> VisibleRegions(ScreenRect viewport) =>
        Visuals(viewport).Select(v => (v.Node, v.Bounds));

    public ScreenVisual Visual(Guid id) => layout[id];
    public bool ContainsNode(Guid id, double x, double y) => Contains(layout[id], x, y);

    public ScreenRect AbsoluteBounds(Guid id)
    {
        var visual = layout[id];
        return ScreenPolygon.Bounds(ScreenPolygon.Transform(ScreenPolygon.Rectangle(new(0, 0, visual.Node.Bounds.Width, visual.Node.Bounds.Height)), visual.World));
    }

    public Guid? PickNode(double x, double y) => visuals.Reverse().FirstOrDefault(v => Contains(v, x, y))?.Node.Id;

    public Guid? HitTest(double x, double y)
    {
        var hit = PointerTarget(x, y);
        return hit?.Kind is ScreenNodeKind.Button or ScreenNodeKind.Area ? hit.Id : null;
    }

    public ScreenNode? PointerTarget(double x, double y) => CanvasViewport.Contains(x, y) ? visuals.Reverse()
        .FirstOrDefault(v => v.Enabled && v.Node.Kind != ScreenNodeKind.Container && Contains(v, x, y))?.Node : null;

    private bool Contains(ScreenVisual visual, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || visual.Polygon.IsEmpty) return false;
        var current = visual;
        var own = true;
        while (true)
        {
            if (own || current.Node.ClipChildren)
            {
                var local = current.ToLocal(x, y);
                if (!new ScreenRect(0, 0, current.Node.Bounds.Width, current.Node.Bounds.Height).Contains(local.X, local.Y)) return false;
            }
            if (current.Node.ParentId is not { } parent) return true;
            current = layout[parent]; own = false;
        }
    }
}
