using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ReviewStage3ProbeTests
{
    [TestMethod]
    public void ReviewScaledButtonIsClickableButHasNoRaster()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
        var button = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Button, new(100, 100, 3000, 500), "Visible button") { Geometry = new() { ScaleX = .1, ScaleY = .1 } };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, button]);
        var rejected = 0;
        Assert.IsEmpty(ScreenTextureCache<Nothing>.Plan(definition, root.Bounds, () => rejected++));
        Assert.AreEqual(1, rejected);
        Assert.AreEqual(button.Id, definition.HitTest(150, 120));
    }

    [TestMethod]
    public void ReviewKeyboardActivatesButtonOutsideViewport()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720)) { ClipChildren = false };
        var button = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Button, new(1500, 100, 200, 50), "Outside viewport");
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, button]);
        Assert.IsFalse(definition.Visuals(root.Bounds).Any(v => v.Node.Id == button.Id));
        var stack = new ScreenStack(Guid.NewGuid()); stack.Open(Guid.NewGuid(), definition);
        stack.Route(new(ScreenInputKind.FocusNext));
        Assert.AreEqual(button.Id, stack.Route(new(ScreenInputKind.Activate)).Activation!.NodeId);
    }
    private sealed class Nothing : IDisposable { public void Dispose() { } }
}
