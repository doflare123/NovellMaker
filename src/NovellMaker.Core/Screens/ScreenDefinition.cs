using System.Collections.Immutable;

namespace NovellMaker.Core.Screens;

public enum ScreenNodeKind { Container, Panel, Text, Button, Image }

/// <summary>Logical pixels; child rectangles are relative to their parent. All parents clip.</summary>
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
}

/// <summary>Immutable, validated screen resource, independent of graphics and script execution.</summary>
public sealed class ScreenDefinition
{
    public const int MaxNodes = 1024;
    public const int MaxDepth = 32;
    public const int MaxTextCharacters = 131072;
    private readonly ImmutableDictionary<Guid, ScreenNode> byId;
    private readonly ImmutableDictionary<Guid, ImmutableArray<ScreenNode>> children;
    private readonly ScreenNode root;

    public ScreenDefinition(Guid id, ImmutableArray<ScreenNode> nodes)
    {
        if (id == Guid.Empty || nodes.IsDefaultOrEmpty || nodes.Length > MaxNodes)
            throw new ArgumentException("Экран должен иметь ID и от 1 до 1024 узлов.");
        var map = new Dictionary<Guid, ScreenNode>();
        var characters = 0;
        foreach (var node in nodes)
        {
            if (node is null || node.Id == Guid.Empty || !map.TryAdd(node.Id, node) ||
                !Enum.IsDefined(node.Kind) || node.ImageId == Guid.Empty || node.Name is null || node.Name.Length > 128 || !node.Bounds.IsValid || node.Text is null || node.Text.Length > 8192)
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
        Nodes = nodes;
        byId = map.ToImmutableDictionary();
        root = nodes.Single(n => n.ParentId is null);
        children = nodes.Where(n => n.ParentId is not null).GroupBy(n => n.ParentId!.Value)
            .ToImmutableDictionary(g => g.Key, g => g.Reverse().ToImmutableArray());
        Buttons = [.. Nodes.Where(n => n.Kind == ScreenNodeKind.Button && IsInteractive(n))];
    }

    public Guid Id { get; }
    /// <summary>Sibling order is paint order; later sibling subtrees are above earlier ones.</summary>
    public ImmutableArray<ScreenNode> Nodes { get; }

    public ImmutableArray<ScreenNode> Buttons { get; }

    /// <summary>Clipped logical rectangles in subtree paint order, shared by menu previews.</summary>
    public IEnumerable<(ScreenNode Node, ScreenRect Bounds)> VisibleRegions(ScreenRect viewport)
    {
        return Visit(root, 0, 0, viewport);
        IEnumerable<(ScreenNode, ScreenRect)> Visit(ScreenNode node, double x, double y, ScreenRect clip)
        {
            if (!node.Visible) yield break;
            x += node.Bounds.X; y += node.Bounds.Y;
            var left = Math.Max(x, clip.X); var top = Math.Max(y, clip.Y);
            var right = Math.Min(x + node.Bounds.Width, clip.X + clip.Width);
            var bottom = Math.Min(y + node.Bounds.Height, clip.Y + clip.Height);
            if (left >= right || top >= bottom) yield break;
            var bounds = new ScreenRect(left, top, right - left, bottom - top);
            yield return (node, bounds);
            foreach (var child in children.GetValueOrDefault(node.Id, []).Reverse())
                foreach (var region in Visit(child, x, y, bounds)) yield return region;
        }
    }

    public ScreenRect AbsoluteBounds(Guid id)
    {
        var node = byId[id]; var bounds = node.Bounds;
        while (node.ParentId is { } parent)
        { node = byId[parent]; bounds = bounds with { X = bounds.X + node.Bounds.X, Y = bounds.Y + node.Bounds.Y }; }
        return bounds;
    }

    public Guid? PickNode(double x, double y) => VisibleRegions(new(-100000, -100000, 200000, 200000))
        .Reverse().FirstOrDefault(r => r.Bounds.Contains(x, y)).Node?.Id;

    public Guid? HitTest(double x, double y)
    {
        // Compare paths, rather than flat positions: a later sibling covers an entire earlier subtree.
        var hit = Hit(root, x, y);
        return hit?.Kind == ScreenNodeKind.Button ? hit.Id : null;
    }

    private ScreenNode? Hit(ScreenNode node, double x, double y)
    {
        if (!node.Visible || !node.Enabled || !node.Bounds.Contains(x, y)) return null;
        foreach (var child in children.GetValueOrDefault(node.Id, []))
        {
            var hit = Hit(child, x - node.Bounds.X, y - node.Bounds.Y);
            if (hit is not null) return hit;
        }
        // Panels and controls occlude earlier siblings; a container itself is transparent.
        return node.Kind == ScreenNodeKind.Container ? null : node;
    }

    private bool IsInteractive(ScreenNode node)
    {
        var left = node.Bounds.X;
        var top = node.Bounds.Y;
        var right = left + node.Bounds.Width;
        var bottom = top + node.Bounds.Height;
        while (true)
        {
            if (!node.Visible || !node.Enabled) return false;
            if (node.ParentId is not { } parent) return true;
            node = byId[parent];
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(node.Bounds.Width, right); bottom = Math.Min(node.Bounds.Height, bottom);
            if (left >= right || top >= bottom) return false;
            left += node.Bounds.X; right += node.Bounds.X;
            top += node.Bounds.Y; bottom += node.Bounds.Y;
        }
    }
}
