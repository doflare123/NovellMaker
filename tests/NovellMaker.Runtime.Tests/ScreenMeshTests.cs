using Microsoft.Xna.Framework;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ScreenMeshTests
{
    [TestMethod]
    public void VerticesUseExactClipAndInverseTextureCoordinatesForRotatedMirroredParent()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
        var panel = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Panel, new(300, 200, 200, 150)) { Geometry = new() { Rotation = 33, ScaleX = -.8, ScaleY = 1.3 } };
        var child = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Button, new(100, 60, 200, 150), "Кириллица");
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, panel, child]); var visual = definition.Visual(child.Id);
        var color = new Color(40, 60, 80, 120); var vertices = ScreenMesh.Build(visual, new(0, 0, 200, 150), color);
        Assert.AreEqual((visual.Polygon.Length - 2) * 3, vertices.Length);
        foreach (var vertex in vertices)
        {
            var local = visual.ToLocal(vertex.Position.X, vertex.Position.Y);
            Assert.AreEqual(local.X / 200, vertex.TextureCoordinate.X, 1e-6); Assert.AreEqual(local.Y / 150, vertex.TextureCoordinate.Y, 1e-6);
            Assert.AreEqual(color, vertex.Color); Assert.IsTrue(vertex.TextureCoordinate.X >= -1e-6 && vertex.TextureCoordinate.X <= .5 + 1e-6);
        }
        var plan = ScreenTextureCache<Nothing>.Plan(definition, root.Bounds).Single();
        Assert.AreEqual(100, plan.Raster.Width); Assert.AreEqual(90, plan.Raster.Height); Assert.AreEqual("Кириллица", plan.Raster.Text);
        var center = visual.World.Apply(30, 30); Assert.AreEqual(child.Id, definition.HitTest(center.X, center.Y));
        Assert.IsEmpty(ScreenMesh.Build(visual, new(500, 500, 10, 10), color));
    }
    private sealed class Nothing : IDisposable { public void Dispose() { } }
}
