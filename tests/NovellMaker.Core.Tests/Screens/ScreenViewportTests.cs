using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenViewportTests
{
    private static ScreenNode Root() => new(Guid.NewGuid(), null, ScreenNodeKind.Container, ScreenDefinition.CanvasViewport) { ClipChildren = false };

    [TestMethod]
    [DataRow(1500, 20)] [DataRow(1280, 20)] [DataRow(-80, 20)] [DataRow(20, 720)] [DataRow(20, -40)]
    public void UnclippedOffCanvasControlsCannotReceiveKeyboardPointerOrLuaClicks(int x, int y)
    {
        foreach (var kind in new[] { ScreenNodeKind.Button, ScreenNodeKind.Area })
        {
            var root = Root(); var button = new ScreenNode(Guid.NewGuid(), root.Id, kind, new(x, y, 80, 40), "Снаружи");
            var definition = new ScreenDefinition(Guid.NewGuid(), [root, button]);
            Assert.IsEmpty(definition.Buttons); Assert.IsNull(definition.HitTest(x + 10, y + 10));
            Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(definition, new("click", button.Id)));
            var stack = new ScreenStack(Guid.NewGuid()); stack.Open(Guid.NewGuid(), definition);
            stack.Route(new(ScreenInputKind.FocusNext)); Assert.IsNull(stack.Focus);
            stack.Route(new(ScreenInputKind.FocusPrevious)); Assert.IsNull(stack.Focus);
            Assert.IsNull(stack.Route(new(ScreenInputKind.Activate)).Activation);
        }
    }

    [TestMethod]
    public void PartiallyVisibleControlsStayFocusableAndViewportEdgesAreExclusive()
    {
        var root = Root(); var outside = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Button, new(1500, 0, 100, 100));
        var visible = outside with { Id = Guid.NewGuid(), Bounds = new(1270, 10, 100, 50) };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, outside, visible]);
        Assert.AreEqual(visible.Id, definition.Buttons.Single().Id);
        Assert.AreEqual(visible.Id, definition.HitTest(1279, 20)); Assert.IsNull(definition.HitTest(1280, 20));
        Assert.IsNull(definition.HitTest(-1, 20)); Assert.IsNull(definition.HitTest(double.NaN, 20));
        var stack = new ScreenStack(Guid.NewGuid()); stack.Open(Guid.NewGuid(), definition);
        stack.Route(new(ScreenInputKind.FocusNext)); Assert.AreEqual(visible.Id, stack.Focus?.NodeId);
        Assert.AreEqual(visible.Id, stack.Route(new(ScreenInputKind.Activate)).Activation?.NodeId);
        stack.Route(new(ScreenInputKind.FocusPrevious)); Assert.AreEqual(visible.Id, stack.Focus?.NodeId);
    }

    [TestMethod]
    public void FocusUsesClippedPolygonInsteadOfBoundingBoxAndCommitClearsCaptureOutsideViewport()
    {
        var root = Root(); var side = 50 * Math.Sqrt(2);
        var button = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Button, new(1320 - side / 2, -40 - side / 2, side, side))
            { Geometry = new() { PivotX = .5, PivotY = .5, Rotation = 45 } };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, button]);
        var box = definition.AbsoluteBounds(button.Id);
        Assert.IsTrue(box.X < 1280 && box.Y + box.Height > 0, "AABB overlaps the canvas corner.");
        Assert.IsEmpty(definition.Buttons);
        var inside = button with { Bounds = new(20, 20, 100, 100), Geometry = new() };
        var stack = new ScreenStack(Guid.NewGuid()); var instance = stack.Open(Guid.NewGuid(), new(definition.Id, [root, inside]));
        stack.Route(new(ScreenInputKind.PointerDown, 30, 30)); Assert.AreEqual(button.Id, stack.Focus?.NodeId);
        stack.Commit(instance, 0, definition);
        Assert.IsNull(stack.Focus); Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, 30, 30)).Activation);
        Assert.IsNull(stack.Route(new(ScreenInputKind.Activate)).Activation);
        stack.Commit(instance, 1, new(definition.Id, [root, inside]));
        stack.Route(new(ScreenInputKind.FocusNext)); Assert.AreEqual(button.Id, stack.Focus?.NodeId);
    }
}
