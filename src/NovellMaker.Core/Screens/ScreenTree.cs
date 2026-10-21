using System.Collections.Immutable;

namespace NovellMaker.Core.Screens;

/// <summary>Pure hierarchy operations. A validated replacement is returned only after the whole edit succeeds.</summary>
public static class ScreenTree
{
    public static ImmutableHashSet<Guid> Subtree(ScreenDefinition definition, Guid id)
    {
        if (!definition.Nodes.Any(n => n.Id == id)) throw new ArgumentException("Узел отсутствует.");
        var children = definition.Nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!.Value);
        var result = ImmutableHashSet.CreateBuilder<Guid>(); var pending = new Stack<Guid>(); pending.Push(id);
        while (pending.TryPop(out var current))
        { result.Add(current); foreach (var child in children[current]) pending.Push(child.Id); }
        return result.ToImmutable();
    }

    public static ScreenDefinition Reparent(ScreenDefinition definition, Guid id, Guid parent)
    {
        var node = definition.Nodes.First(n => n.Id == id);
        if (node.ParentId is null) throw new ArgumentException("Корень нельзя перемещать.");
        return new(definition.Id, definition.Nodes.Replace(node, node with { ParentId = parent }), definition.RetiredNodeIds);
    }

    public static ScreenDefinition Remove(ScreenDefinition definition, Guid id)
    {
        var removed = Subtree(definition, id);
        if (definition.Nodes.Any(n => n.Id == id && n.ParentId is null)) throw new ArgumentException("Корень нельзя удалить.");
        return new(definition.Id, [.. definition.Nodes.Where(n => !removed.Contains(n.Id))], definition.RetiredNodeIds.Union(removed));
    }

    public static (ScreenDefinition Definition, ImmutableDictionary<Guid, Guid> Ids) Copy(ScreenDefinition definition, Guid id,
        Guid parent, ImmutableDictionary<Guid, Guid>? ids = null)
    {
        var copied = Subtree(definition, id);
        ids ??= copied.ToImmutableDictionary(key => key, _ => Guid.NewGuid());
        if (ids.Count != copied.Count || copied.Any(key => !ids.ContainsKey(key)) || ids.Values.Any(value => value == Guid.Empty || definition.RetiredNodeIds.Contains(value) || definition.Nodes.Any(n => n.Id == value)) || ids.Values.Distinct().Count() != ids.Count)
            throw new ArgumentException("Недопустимые ID копии поддерева.");
        var nodes = definition.Nodes.AddRange(definition.Nodes.Where(n => copied.Contains(n.Id))
            .Select(n => n with { Id = ids[n.Id], ParentId = n.Id == id ? parent : ids[n.ParentId!.Value] }));
        return (new(definition.Id, nodes, definition.RetiredNodeIds), ids);
    }

    public static ScreenRect Resize(ScreenNode node, double dx, double dy)
    {
        var width = Math.Clamp(Math.Round(node.Bounds.Width + dx), 1, 100000);
        var height = Math.Clamp(Math.Round(node.Bounds.Height + dy), 1, 100000);
        var matrix = node.Geometry.Matrix(node.Bounds, 0, 0);
        var x = (width - node.Bounds.Width) * node.Geometry.PivotX;
        var y = (height - node.Bounds.Height) * node.Geometry.PivotY;
        return node.Bounds with { X = node.Bounds.X + (matrix.A - 1) * x + matrix.C * y,
            Y = node.Bounds.Y + matrix.B * x + (matrix.D - 1) * y, Width = width, Height = height };
    }
}
