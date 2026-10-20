using System.Collections.Immutable;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenTests
{
    private static Guid Id() => Guid.NewGuid();
    private static ScreenNode Root() => new(Id(), null, ScreenNodeKind.Container, new(10, 20, 200, 150));
    private static ScreenNode Button(Guid parent, int x = 0) => new(Id(), parent, ScreenNodeKind.Button, new(x, 10, 40, 30), "OK");
    private static ScreenDefinition Screen()
    {
        var root = Root();
        return new(Id(), [root, Button(root.Id), Button(root.Id, 60)]);
    }

    [TestMethod]
    public void DefinitionUsesStableIdsClippedRelativeGeometryAndSiblingSubtrees()
    {
        var root = Root(); var first = Button(root.Id);
        var panel = new ScreenNode(Id(), root.Id, ScreenNodeKind.Panel, new(0, 0, 80, 80));
        var child = Button(panel.Id);
        // Flat order does not change subtree z order. The later panel covers first.
        var definition = new ScreenDefinition(Id(), [root, first, panel, child]);
        Assert.AreEqual(child.Id, definition.HitTest(20, 40));
        Assert.IsNull(definition.HitTest(10, 20));
        Assert.IsNull(definition.HitTest(210, 40));
        Assert.IsNull(definition.HitTest(double.NaN, 40));
        Assert.AreEqual(2, definition.Buttons.Length);
        var blocked = new ScreenDefinition(Id(), [root, first, panel]);
        Assert.IsNull(blocked.HitTest(20, 40), "Panel occludes an earlier button.");
        var text = panel with { Kind = ScreenNodeKind.Text, Text = "label" };
        Assert.IsNull(new ScreenDefinition(Id(), [root, first, text]).HitTest(20, 40));
        var earlierChild = Button(panel.Id);
        Assert.AreEqual(first.Id, new ScreenDefinition(Id(), [root, panel, first, earlierChild]).HitTest(20, 40));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void AncestorVisibilityAndEnabledGatePointerAndKeyboard(bool visible, bool enabled)
    {
        var root = Root() with { Visible = visible, Enabled = enabled }; var button = Button(root.Id);
        var screen = new ScreenDefinition(Id(), [root, button]);
        Assert.IsNull(screen.HitTest(20, 40));
        Assert.IsTrue(screen.Buttons.IsEmpty);
        var stack = new ScreenStack(Id()); stack.Open(Id(), screen);
        Assert.IsNull(stack.Route(new(ScreenInputKind.Activate)).Activation);
    }

    [TestMethod]
    public void FullyClippedButtonsAreNotFocusableAndPartialClipHasExclusiveEdges()
    {
        var root = Root(); var outside = Button(root.Id, 201); var partial = Button(root.Id, 190);
        var screen = new ScreenDefinition(Id(), [root, outside, partial]);
        Assert.AreEqual(partial.Id, screen.Buttons.Single().Id);
        Assert.AreEqual(partial.Id, screen.HitTest(209, 31));
        Assert.IsNull(screen.HitTest(210, 31));
        var below = Button(root.Id) with { Bounds = new(0, 200, 10, 10) };
        Assert.IsTrue(new ScreenDefinition(Id(), [root, below]).Buttons.IsEmpty);
    }

    [TestMethod]
    public void DefinitionRejectsMalformedIdsTreeAndPayload()
    {
        var root = Root(); var button = Button(root.Id);
        void Bad(params ScreenNode[] nodes) => Assert.ThrowsExactly<ArgumentException>(() => new ScreenDefinition(Id(), [.. nodes]));
        Assert.ThrowsExactly<ArgumentException>(() => new ScreenDefinition(Guid.Empty, [root]));
        Assert.ThrowsExactly<ArgumentException>(() => new ScreenDefinition(Id(), default));
        Assert.ThrowsExactly<ArgumentException>(() => new ScreenDefinition(Id(), []));
        Bad((ScreenNode)null!); Bad(root with { Id = Guid.Empty }); Bad(root, root);
        Bad(root with { Kind = (ScreenNodeKind)99 }); Bad(root with { Text = null! });
        Bad(root with { Text = new string('x', 8193) });
        Bad(root, root with { Id = Id() });
        Bad(root, button with { ParentId = Id() });
        Bad(root, button, Button(button.Id));
        Bad(root with { ParentId = root.Id });
        var a = Root(); var b = Root();
        Bad(root, a with { ParentId = b.Id }, b with { ParentId = a.Id });
        Bad(Enumerable.Range(0, 1025).Select(_ => Root()).ToArray());
        Bad([root, .. Enumerable.Range(0, 17).Select(_ => Button(root.Id) with { Text = new string('x', 8192) })]);
    }

    [TestMethod]
    [DataRow(double.NaN, 0, 1, 1)]
    [DataRow(0, double.PositiveInfinity, 1, 1)]
    [DataRow(0, 0, double.NegativeInfinity, 1)]
    [DataRow(0, 0, 1, double.NaN)]
    [DataRow(100001, 0, 1, 1)]
    [DataRow(0, -100001, 1, 1)]
    [DataRow(0, 0, 0, 1)]
    [DataRow(0, 0, 100001, 1)]
    [DataRow(0, 0, 1, 0)]
    [DataRow(0, 0, 1, 100001)]
    public void InvalidRectanglesAreRejected(double x, double y, double width, double height) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ScreenDefinition(Id(), [Root() with { Bounds = new(x, y, width, height) }]));

    [TestMethod]
    public void ExactNodeTextAndDepthBudgetsAreAcceptedNextItemRejected()
    {
        var root = Root();
        var nodes = Enumerable.Range(0, 1023).Select(_ => Button(root.Id)).Prepend(root).ToImmutableArray();
        Assert.AreEqual(1024, new ScreenDefinition(Id(), nodes).Nodes.Length);
        var text = new ScreenDefinition(Id(), [root, .. Enumerable.Range(0, 16).Select(_ => Button(root.Id) with { Text = new string('a', 8192) })]);
        Assert.AreEqual(ScreenDefinition.MaxTextCharacters, text.Nodes.Sum(n => n.Text.Length));
        var chain = ImmutableArray.CreateBuilder<ScreenNode>(); chain.Add(root);
        for (var i = 1; i < 32; i++) chain.Add(Root() with { ParentId = chain[^1].Id });
        _ = new ScreenDefinition(Id(), chain.ToImmutable());
        chain.Add(Button(chain[^1].Id));
        Assert.ThrowsExactly<ArgumentException>(() => new ScreenDefinition(Id(), chain.ToImmutable()));
    }

    [TestMethod]
    public void OwnerIdentityReplaceBackAndClearInvalidateOldHandlesAtomically()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ScreenStack(Guid.Empty));
        var stack = new ScreenStack(Id()); var owner = Id(); var screen = Screen();
        Assert.ThrowsExactly<ArgumentNullException>(() => stack.Open(owner, null!));
        Assert.ThrowsExactly<ArgumentException>(() => stack.Open(Guid.Empty, screen));
        var first = stack.Open(owner, screen, new(false, false));
        var second = stack.Open(Id(), screen);
        Assert.AreEqual(stack.SessionId, first.SessionId);
        Assert.AreEqual(owner, first.OwnerId); Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(first.Definition.Nodes[1].Id, second.Definition.Nodes[1].Id);
        Assert.IsTrue(stack.PausesScenario);
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Close(first with { }));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ScreenStack(Id()).Close(first));
        Assert.ThrowsExactly<ArgumentNullException>(() => stack.Replace(first, null!));
        Assert.AreSame(first, stack.Instances[0]);
        var replacement = stack.Replace(first, screen);
        Assert.AreEqual(first.Policy, replacement.Policy); Assert.AreEqual(first.OwnerId, replacement.OwnerId);
        Assert.AreNotEqual(first.Id, replacement.Id);
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Close(first));
        Assert.IsTrue(stack.Route(new(ScreenInputKind.Back)).Consumed);
        Assert.AreSame(replacement, stack.Instances.Single()); Assert.IsFalse(stack.PausesScenario);
        stack.CloseOwner(Id()); Assert.AreEqual(1, stack.Instances.Length);
        stack.CloseOwner(owner); Assert.IsTrue(stack.Instances.IsEmpty);
        Assert.IsFalse(stack.Route(new(ScreenInputKind.Back)).Consumed);
        stack.Open(owner, screen); stack.Clear();
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Replace(replacement, screen));
    }

    [TestMethod]
    public void SessionNodeAndStackBudgetsRejectWithoutChangingStateAndReplaceSubtractsOldNodes()
    {
        var root = Root(); var large = new ScreenDefinition(Id(), [root, .. Enumerable.Range(0, 1023).Select(_ => Button(root.Id))]);
        var stack = new ScreenStack(Id()); var owner = Id();
        for (var i = 0; i < 4; i++) stack.Open(owner, large);
        var before = stack.Instances;
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Open(owner, Screen()));
        Assert.IsTrue(before == stack.Instances, "Rejected operation must retain the same immutable snapshot.");
        var small = stack.Replace(before[0], Screen(), new(false, false));
        stack.Open(owner, Screen());
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Replace(small, large));
        stack.Clear();
        for (var i = 0; i < 32; i++) stack.Open(owner, Screen());
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Open(owner, Screen()));
        Assert.AreEqual(32, stack.Instances.Length);
    }

    [TestMethod]
    public void NonmodalPassesEmptySpaceButModalBlocksStoryAndLowerScreens()
    {
        var stack = new ScreenStack(Id()); var screen = Screen();
        Assert.IsFalse(stack.Route(new(ScreenInputKind.Activate)).Consumed);
        var lower = stack.Open(Id(), screen);
        var overlay = new ScreenDefinition(Id(), [Root()]);
        var upper = stack.Open(Id(), overlay, new(false, false));
        stack.Route(new(ScreenInputKind.PointerDown, 20, 40));
        Assert.AreEqual(lower.Id, stack.Route(new(ScreenInputKind.PointerUp, 20, 40)).Activation?.InstanceId);
        Assert.AreEqual(lower.Id, stack.Route(new(ScreenInputKind.Activate)).Activation?.InstanceId);
        stack.Replace(upper, overlay, new(true, false));
        Assert.IsNull(stack.Route(new(ScreenInputKind.Activate)).Activation);
        Assert.IsTrue(stack.Route(new(ScreenInputKind.PointerDown, 999, 999)).Consumed);
        stack.Clear(); stack.Open(Id(), overlay, new(false, false));
        Assert.IsFalse(stack.Route(new(ScreenInputKind.PointerUp, 999, 999)).Consumed);
        Assert.IsFalse(stack.Route(new(ScreenInputKind.FocusNext)).Consumed);
    }

    [TestMethod]
    public void KeyboardWrapsBothDirectionsAndFocusIsIndependentForInstances()
    {
        var stack = new ScreenStack(Id()); var resource = Screen(); var lower = stack.Open(Id(), resource);
        var first = resource.Nodes[1].Id; var second = resource.Nodes[2].Id;
        Assert.AreEqual(first, stack.Route(new(ScreenInputKind.Activate)).Activation?.NodeId);
        stack.Route(new(ScreenInputKind.FocusPrevious)); Assert.AreEqual(second, stack.Focus?.NodeId);
        stack.Route(new(ScreenInputKind.FocusPrevious)); Assert.AreEqual(first, stack.Focus?.NodeId);
        stack.Route(new(ScreenInputKind.FocusNext)); Assert.AreEqual(second, stack.Focus?.NodeId);
        stack.Route(new(ScreenInputKind.FocusNext)); Assert.AreEqual(first, stack.Focus?.NodeId);
        var upper = stack.Open(Id(), resource); Assert.IsNull(stack.Focus);
        stack.Route(new(ScreenInputKind.FocusPrevious)); Assert.AreEqual(second, stack.Focus?.NodeId);
        Assert.AreEqual(upper.Id, stack.Route(new(ScreenInputKind.Activate)).Activation?.InstanceId);
        stack.Close(upper); Assert.IsNull(stack.Focus);
        Assert.AreEqual(lower.Id, stack.Route(new(ScreenInputKind.Activate)).Activation?.InstanceId);
    }

    [TestMethod]
    public void PointerRequiresMatchingPressReleaseAndFocusLossOrTopologyDiscardsCapture()
    {
        var stack = new ScreenStack(Id()); var screen = stack.Open(Id(), Screen());
        Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 20, 40)).Activation);
        stack.Route(new(ScreenInputKind.PointerDown, 20, 40));
        Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 80, 40)).Activation);
        stack.Route(new(ScreenInputKind.PointerDown, 20, 40));
        Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, -1, -1)).Activation);
        stack.Route(new(ScreenInputKind.PointerDown, 20, 40)); stack.SetActive(false);
        Assert.IsNull(stack.Focus); Assert.IsTrue(stack.Route(new(ScreenInputKind.Activate)).Consumed);
        stack.SetActive(false); stack.SetActive(true);
        Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 20, 40)).Activation);
        stack.Route(new(ScreenInputKind.PointerDown, 20, 40)); stack.Clear();
        Assert.IsFalse(stack.Route(new(ScreenInputKind.PointerUp, 20, 40)).Consumed);
        stack.Open(Id(), Screen());
        stack.Route(new(ScreenInputKind.PointerDown, 20, 40));
        var top = stack.Open(Id(), Screen()); stack.Close(top);
        Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 20, 40)).Activation);
        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Close(screen));
        Assert.ThrowsExactly<ArgumentException>(() => stack.Route(new((ScreenInputKind)99)));
        Assert.ThrowsExactly<ArgumentException>(() => stack.Route(new(ScreenInputKind.PointerDown, double.NaN)));
        Assert.ThrowsExactly<ArgumentException>(() => stack.Route(new(ScreenInputKind.PointerDown, 0, double.PositiveInfinity)));
    }

    [TestMethod]
    public void RepeatedOwnerCleanupLeavesNoInstancesFocusOrPressedControls()
    {
        var stack = new ScreenStack(Id()); var owner = Id(); var screen = Screen();
        for (var i = 0; i < 2000; i++)
        {
            stack.Open(owner, screen); stack.Open(owner, screen);
            stack.Route(new(ScreenInputKind.PointerDown, 20, 40));
            stack.CloseOwner(owner);
            Assert.IsTrue(stack.Instances.IsEmpty); Assert.IsNull(stack.Focus);
            Assert.IsFalse(stack.Route(new(ScreenInputKind.PointerUp, 20, 40)).Consumed);
        }
    }
}
