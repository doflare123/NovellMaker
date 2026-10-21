using System.Collections.Immutable;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class LiveNodeTests
{
    private static ScreenChange Geometry(Guid id, ScreenNodePatch patch) => new("geometry", id, null, null) { Patch = patch };

    [TestMethod]
    public void PatchChangesOnlyWhitelistedPropertiesAndPreservesIdsAndText()
    {
        var before = ScreenLuaProtocolTests.Definition(); var node = before.Nodes[1];
        var patch = new ScreenNodePatch { X = 11, Y = 12, Width = 60, Height = 40, AnchorX = .1, AnchorY = .2, PivotX = .3, PivotY = .4,
            ScaleX = -.5, ScaleY = .6, Rotation = 20, Opacity = .8, Color = "#123456", Order = -2, ClipChildren = false };
        var prepared = ScreenLuaProtocol.Prepare(before, [Geometry(node.Id, patch)]).Definition;
        var changed = prepared.Nodes[1];
        Assert.AreEqual(node.Id, changed.Id); Assert.AreEqual(node.ParentId, changed.ParentId); Assert.AreEqual(node.Text, changed.Text);
        Assert.AreEqual(new ScreenRect(11, 12, 60, 40), changed.Bounds); Assert.AreEqual(.1, changed.Geometry.AnchorX); Assert.AreEqual(.2, changed.Geometry.AnchorY);
        Assert.AreEqual(.3, changed.Geometry.PivotX); Assert.AreEqual(.4, changed.Geometry.PivotY); Assert.AreEqual(-.5, changed.Geometry.ScaleX); Assert.AreEqual(.6, changed.Geometry.ScaleY);
        Assert.AreEqual(20, changed.Geometry.Rotation); Assert.AreEqual(.8, changed.Geometry.Opacity); Assert.AreEqual("#123456", changed.Color); Assert.AreEqual(-2, changed.Order); Assert.IsFalse(changed.ClipChildren);
        Assert.AreSame(prepared, ScreenLuaProtocol.Prepare(prepared, [Geometry(node.Id, new())]).Definition);
        Assert.AreEqual(new ScreenRect(10, 10, 50, 30), node.Bounds);
    }

    [TestMethod]
    public void CopyMoveRemoveAndDeletedRefsAreAtomicAndCannotResurrectIds()
    {
        var before = ScreenLuaProtocolTests.Definition(); var root = before.Nodes[0]; var button = before.Nodes[1]; var copyId = Guid.NewGuid();
        var map = ImmutableDictionary<Guid, Guid>.Empty.Add(button.Id, copyId);
        var copied = ScreenLuaProtocol.Prepare(before, [new("copy", button.Id, null, null) { ParentId = root.Id, Ids = map }, Geometry(copyId, new() { X = 20 }), new("parent", copyId, null, null) { ParentId = root.Id }]).Definition;
        Assert.HasCount(4, copied.Nodes); Assert.AreEqual(20, copied.Nodes.Last().Bounds.X);
        var removed = ScreenLuaProtocol.Prepare(copied, [new("remove", copyId, null, null)]).Definition;
        Assert.HasCount(3, removed.Nodes);
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.Prepare(removed, [new("copy", button.Id, null, null) { ParentId = root.Id, Ids = map }]));
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(copied, [new("remove", copyId, null, null), new("text", copyId, "deleted", null)]));
        Assert.AreEqual(4, copied.Nodes.Length);
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.Prepare(before, [new("text", button.Id, "partial", null), new("parent", root.Id, null, null) { ParentId = button.Id }]));
        Assert.AreEqual("first", button.Text);
    }

    [TestMethod]
    public void EveryMalformedExtendedCommandAndNonFinitePatchRejectsWholeBatch()
    {
        var before = ScreenLuaProtocolTests.Definition(); var node = before.Nodes[1];
        var invalid = new ScreenChange[] {
            Geometry(node.Id, new() { X = double.NaN }), Geometry(node.Id, new() { Y = double.PositiveInfinity }),
            Geometry(node.Id, new() { Width = -1 }), Geometry(node.Id, new() { Height = double.NaN }),
            Geometry(node.Id, new() { AnchorX = 2 }), Geometry(node.Id, new() { Color = "no" }),
            Geometry(node.Id, new() { Order = int.MaxValue }), Geometry(node.Id, new() { ScaleY = 0 }),
            Geometry(node.Id, new() { Opacity = -1 }), new("geometry", node.Id, null, null),
            Geometry(node.Id, new()) with { Text = "extra" }, Geometry(node.Id, new()) with { Value = false },
            Geometry(node.Id, new()) with { ParentId = before.Nodes[0].Id }, Geometry(node.Id, new()) with { Ids = ImmutableDictionary<Guid, Guid>.Empty },
            new("copy", node.Id, null, null) { ParentId = before.Nodes[0].Id },
            new("copy", node.Id, null, null) { Ids = ImmutableDictionary<Guid, Guid>.Empty },
            new("copy", node.Id, null, null) { ParentId = before.Nodes[0].Id, Ids = ImmutableDictionary<Guid, Guid>.Empty },
            new("remove", node.Id, "extra", null), new("remove", node.Id, null, false),
            new("parent", node.Id, null, null), new("parent", node.Id, null, null) { ParentId = Guid.NewGuid() },
            new("close", null, null, null) { Patch = new() }, new("text", node.Id, "extra", null) { Patch = new() },
            new("visible", node.Id, null, true) { Ids = ImmutableDictionary<Guid, Guid>.Empty }
        };
        foreach (var change in invalid)
        { Assert.Throws<Exception>(() => ScreenLuaProtocol.Prepare(before, [new("text", node.Id, "partial", null), change])); Assert.AreEqual("first", before.Nodes[1].Text); }
    }

    [TestMethod]
    public void HandleRequiresAllThreeIdentitiesAndRejectsRawAndMalformedIds()
    {
        var handle = new ScreenNodeHandle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.IsTrue(ScreenNodeHandle.TryParse(handle.ToString(), out var parsed)); Assert.AreEqual(handle, parsed);
        foreach (var text in new[] { "", handle.NodeId.ToString(), "a/b/c", handle.ToString() + "/extra", new ScreenNodeHandle(Guid.Empty, handle.InstanceId, handle.NodeId).ToString(), new ScreenNodeHandle(handle.ResourceId, Guid.Empty, handle.NodeId).ToString(), new ScreenNodeHandle(handle.ResourceId, handle.InstanceId, Guid.Empty).ToString() })
            Assert.IsFalse(ScreenNodeHandle.TryParse(text, out _));
    }

    [TestMethod]
    public void DeletedIdentityHistoryIsBoundedAndSurvivesCreateRemoveNoOpPackets()
    {
        var before = ScreenLuaProtocolTests.Definition(); var button = before.Nodes[1]; var root = before.Nodes[0]; var id = Guid.NewGuid();
        var copy = new ScreenChange("copy", button.Id, null, null) { ParentId = root.Id, Ids = ImmutableDictionary<Guid, Guid>.Empty.Add(button.Id, id) };
        var after = ScreenLuaProtocol.Prepare(before, [copy, new("remove", id, null, null)]).Definition;
        Assert.AreNotSame(before, after); Assert.IsTrue(before.Nodes.SequenceEqual(after.Nodes)); Assert.IsTrue(after.RetiredNodeIds.Contains(id));
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.Prepare(after, [copy]));
        var retired = Enumerable.Range(0, ScreenDefinition.MaxNodeIdentities - before.Nodes.Length).Select(_ => Guid.NewGuid()).ToImmutableHashSet();
        var full = new ScreenDefinition(before.Id, before.Nodes, retired);
        Assert.Throws<ArgumentException>(() => ScreenTree.Copy(full, button.Id, root.Id));
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(before.Id, before.Nodes, retired.Add(Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(before.Id, before.Nodes, ImmutableHashSet<Guid>.Empty.Add(button.Id)));
    }

    [TestMethod]
    public void StackCommitChecksAggregateBudgetAfterBackgroundPreparationAndClearsRemovedCapture()
    {
        var stack = new ScreenStack(Guid.NewGuid()); var before = ScreenLuaProtocolTests.Definition();
        var instance = stack.Open(Guid.NewGuid(), before);
        var root = before.Nodes[0]; var large = new ScreenDefinition(Guid.NewGuid(), [root, .. Enumerable.Range(0, 1023).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Area, new(0, 0, 1, 1)))]);
        for (var i = 0; i < 3; i++) stack.Open(Guid.NewGuid(), large);
        var almostFull = new ScreenDefinition(Guid.NewGuid(), [root, .. Enumerable.Range(0, 1020).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Area, new(0, 0, 1, 1)))]);
        var last = stack.Open(Guid.NewGuid(), almostFull);
        var copied = ScreenTree.Copy(before, before.Nodes[1].Id, root.Id).Definition;
        Assert.Throws<InvalidOperationException>(() => stack.Commit(instance, 0, copied)); Assert.AreSame(before, instance.Definition); Assert.AreEqual(0L, instance.Version);
        Assert.Throws<InvalidOperationException>(() => stack.Commit(instance, 0, large));
        stack.Close(last); stack.CloseOwner(stack.Instances[1].OwnerId); stack.CloseOwner(stack.Instances[1].OwnerId); stack.CloseOwner(stack.Instances[1].OwnerId);
        stack.Route(new(ScreenInputKind.PointerDown, 20, 20));
        stack.Commit(instance, 0, ScreenTree.Remove(before, before.Nodes[1].Id));
        Assert.IsNull(stack.Focus); Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 20, 20)).Activation);
        Assert.Throws<InvalidOperationException>(() => stack.Commit(instance, 0, before));
    }
}
