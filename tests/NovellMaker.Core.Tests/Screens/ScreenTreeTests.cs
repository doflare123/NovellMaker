using System.Collections.Immutable;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenTreeTests
{
    [TestMethod]
    public void CopyReparentAndRemovePreserveOutsideIdsAndRejectCyclesOrRootEdits()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
        var panel = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Panel, new(10, 20, 100, 80));
        var child = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Area, new(0, 0, 20, 20));
        var other = panel with { Id = Guid.NewGuid(), ParentId = root.Id };
        var definition = new ScreenDefinition(Guid.NewGuid(), [child, root, panel, other]);
        var copy = ScreenTree.Copy(definition, panel.Id, other.Id);
        Assert.HasCount(6, copy.Definition.Nodes); Assert.HasCount(2, copy.Ids);
        Assert.AreEqual(other.Id, copy.Definition.Nodes.Single(n => n.Id == copy.Ids[panel.Id]).ParentId);
        Assert.AreEqual(copy.Ids[panel.Id], copy.Definition.Nodes.Single(n => n.Id == copy.Ids[child.Id]).ParentId);
        Assert.AreSame(child, copy.Definition.Nodes.Single(n => n.Id == child.Id));
        var moved = ScreenTree.Reparent(copy.Definition, panel.Id, other.Id);
        Assert.AreEqual(panel.Bounds, moved.Nodes.Single(n => n.Id == panel.Id).Bounds);
        var removed = ScreenTree.Remove(moved, other.Id); Assert.AreEqual(root, removed.Nodes.Single());
        Assert.Throws<ArgumentException>(() => ScreenTree.Reparent(definition, panel.Id, panel.Id));
        Assert.Throws<ArgumentException>(() => ScreenTree.Reparent(definition, panel.Id, child.Id));
        Assert.Throws<ArgumentException>(() => ScreenTree.Reparent(definition, panel.Id, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => ScreenTree.Reparent(definition, root.Id, panel.Id));
        Assert.Throws<ArgumentException>(() => ScreenTree.Remove(definition, root.Id));
        Assert.Throws<ArgumentException>(() => ScreenTree.Subtree(definition, Guid.NewGuid()));
        Assert.HasCount(4, definition.Nodes);
    }

    [TestMethod]
    public void InvalidCopyMapsAndNodeDepthBudgetsRejectWithoutPublishingAnything()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 10, 10));
        var child = root with { Id = Guid.NewGuid(), ParentId = root.Id };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, child]);
        var fresh = Guid.NewGuid();
        foreach (var ids in new[] { ImmutableDictionary<Guid, Guid>.Empty,
            ImmutableDictionary<Guid, Guid>.Empty.Add(Guid.NewGuid(), fresh),
            ImmutableDictionary<Guid, Guid>.Empty.Add(child.Id, Guid.Empty),
            ImmutableDictionary<Guid, Guid>.Empty.Add(child.Id, root.Id) })
            Assert.Throws<ArgumentException>(() => ScreenTree.Copy(definition, child.Id, root.Id, ids));
        Assert.Throws<ArgumentException>(() => ScreenTree.Copy(definition, root.Id, root.Id, ImmutableDictionary<Guid, Guid>.Empty.Add(root.Id, fresh).Add(child.Id, fresh)));
        var nodes = definition.Nodes;
        for (var i = 2; i <= 32; i++) nodes = nodes.Add(child with { Id = Guid.NewGuid(), ParentId = nodes[^1].Id });
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(definition.Id, nodes));
        definition = new(definition.Id, [root, .. Enumerable.Range(0, 1023).Select(_ => child with { Id = Guid.NewGuid() })]);
        Assert.Throws<ArgumentException>(() => ScreenTree.Copy(definition, definition.Nodes[1].Id, root.Id));
        Assert.HasCount(1024, definition.Nodes);
    }

    [TestMethod]
    public void ResizeKeepsTransformedOriginFixedAndClampsDimensions()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
        var node = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Panel, new(100, 100, 200, 150)) { Geometry = new() { PivotX = .5, PivotY = 1, ScaleX = -2, ScaleY = .5, Rotation = 40 } };
        var before = new ScreenDefinition(Guid.NewGuid(), [root, node]);
        var resized = node with { Bounds = ScreenTree.Resize(node, 50, -30) };
        var after = new ScreenDefinition(before.Id, [root, resized]);
        var a = before.Visual(node.Id).World.Apply(0, 0); var b = after.Visual(node.Id).World.Apply(0, 0);
        Assert.AreEqual(a.X, b.X, 1e-8); Assert.AreEqual(a.Y, b.Y, 1e-8);
        Assert.AreEqual(250, resized.Bounds.Width); Assert.AreEqual(120, resized.Bounds.Height);
        Assert.AreEqual(1, ScreenTree.Resize(node, -999999, 0).Width); Assert.AreEqual(100000, ScreenTree.Resize(node, 0, 999999).Height);
    }
}
