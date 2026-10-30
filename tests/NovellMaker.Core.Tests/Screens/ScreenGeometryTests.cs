using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenGeometryTests
{
    private static ScreenNode Root() => new(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
    private static ScreenNode Child(ScreenNode parent, ScreenNodeKind kind = ScreenNodeKind.Area) => new(Guid.NewGuid(), parent.Id, kind, new(10, 20, 100, 50));
    private static void Near(ScreenPoint expected, ScreenPoint actual)
    { Assert.AreEqual(expected.X, actual.X, 1e-7); Assert.AreEqual(expected.Y, actual.Y, 1e-7); }

    [TestMethod]
    public void NestedAnchorsPivotRotationAndMirrorHaveExactInverseAndLocalHit()
    {
        var root = Root();
        var panel = Child(root, ScreenNodeKind.Panel) with { Bounds = new(20, -10, 300, 200), Geometry = new() { AnchorX = .5, AnchorY = .5, PivotX = .5, PivotY = .5, ScaleX = -1.2, ScaleY = .7, Rotation = 37 } };
        var child = Child(panel) with { Bounds = new(-50, -25, 100, 50), Geometry = new() { AnchorX = .5, AnchorY = .5, Rotation = -17 } };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, panel, child]);
        var visual = definition.Visual(child.Id);
        foreach (var point in new ScreenPoint[] { new(0, 0), new(50, 25), new(100, 50), new(-30, 70) })
        { var world = visual.World.Apply(point.X, point.Y); Near(point, visual.ToLocal(world.X, world.Y)); }
        var center = visual.World.Apply(50, 25);
        Assert.AreEqual(child.Id, definition.HitTest(center.X, center.Y));
        Assert.AreEqual(child.Id, definition.PickNode(center.X, center.Y));
        var outside = visual.World.Apply(101, 25);
        Assert.IsNull(definition.HitTest(outside.X, outside.Y));
        Assert.AreEqual(new ScreenPoint(20 + 640 + 150, -10 + 360 + 100), definition.Visual(panel.Id).World.Apply(150, 100));
        Assert.Throws<ArgumentException>(() => new ScreenMatrix(0, 0, 0, 0, 0, 0).Inverse());
    }

    [TestMethod]
    public void RotatedClippingIsPolygonalAndOptionalForDescendants()
    {
        var root = Root(); var panel = Child(root, ScreenNodeKind.Panel) with { Bounds = new(200, 200, 100, 100), Geometry = new() { Rotation = 45 } };
        var child = Child(panel) with { Bounds = new(60, 60, 100, 100) };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, panel, child]);
        var visual = definition.Visual(child.Id);
        Assert.AreEqual(1600, Math.Abs(ScreenPolygon.Area(visual.Polygon)), 1e-7);
        var inside = visual.World.Apply(10, 10); var outside = visual.World.Apply(80, 80);
        Assert.AreEqual(child.Id, definition.HitTest(inside.X, inside.Y));
        Assert.IsNull(definition.HitTest(outside.X, outside.Y));
        Assert.IsTrue(visual.Bounds.Width < definition.AbsoluteBounds(child.Id).Width);
        definition = new(definition.Id, [root, panel with { ClipChildren = false }, child]);
        Assert.AreEqual(child.Id, definition.HitTest(outside.X, outside.Y));
        Assert.AreEqual(10000, Math.Abs(ScreenPolygon.Area(definition.Visual(child.Id).Polygon)), 1e-7);
        Assert.IsEmpty(definition.Visuals(new(0, 0, 1, 1)).Where(v => v.Node.Id == child.Id).ToArray());
    }

    [TestMethod]
    public void OrderIsStableAcrossWholeSubtreesAndDisabledInvisibleOrTransparentAncestorsGateInput()
    {
        var root = Root(); var first = Child(root) with { Bounds = new(0, 0, 200, 200), Order = 3 };
        var panel = Child(root, ScreenNodeKind.Panel) with { Bounds = first.Bounds, Order = 1 };
        var nested = Child(panel) with { Bounds = first.Bounds, Order = 100 };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, first, panel, nested]);
        Assert.AreEqual(first.Id, definition.HitTest(30, 30));
        foreach (var unavailable in new[] { first with { Visible = false }, first with { Enabled = false }, first with { Geometry = new() { Opacity = 0 } } })
        { definition = new(definition.Id, [root, unavailable, panel, nested]); Assert.AreEqual(nested.Id, definition.HitTest(30, 30)); }
        definition = new(definition.Id, [root with { Enabled = false }, first, panel, nested]);
        Assert.IsEmpty(definition.Buttons); Assert.IsNull(definition.PointerTarget(30, 30)); Assert.AreEqual(first.Id, definition.PickNode(30, 30));
        definition = new(definition.Id, [root, first, panel with { Order = 3 }, nested]);
        Assert.AreEqual(nested.Id, definition.HitTest(30, 30));
    }

    [TestMethod]
    public void ColorOpacityAndClipVisibilityAreInheritedWithoutMutatingTemplates()
    {
        var root = Root() with { Color = "#8080FF", Geometry = new() { Opacity = .5 } };
        var panel = Child(root, ScreenNodeKind.Panel) with { Color = "#FF8040", Geometry = new() { Opacity = .25 } };
        var child = Child(panel);
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, panel, child]);
        var visual = definition.Visual(child.Id);
        Assert.AreEqual(.125, visual.Opacity); Assert.AreEqual(128d / 255, visual.Tint.R); Assert.AreEqual(128d * 128 / 255 / 255, visual.Tint.G); Assert.AreEqual(64d / 255, visual.Tint.B);
        Assert.AreEqual("#FFFFFF", child.Color);
        foreach (var hidden in new[] { root with { Visible = false }, root with { Geometry = new() { Opacity = 0 } } })
        { definition = new(definition.Id, [hidden, panel, child]); Assert.IsEmpty(definition.Visuals(root.Bounds).ToArray()); Assert.IsNull(definition.PickNode(25, 45)); }
        Assert.IsFalse(ScreenTint.TryParse(null, out _)); Assert.IsFalse(ScreenTint.TryParse("#GGGGGG", out _));
        Assert.AreEqual(new ScreenRect(0, 0, 0, 0), ScreenPolygon.Bounds([]));
        Assert.IsEmpty(ScreenPolygon.Clip(ScreenPolygon.Rectangle(root.Bounds), []));
    }

    [TestMethod]
    public void AllNonFiniteAndOutOfRangeGeometryIsRejectedIncludingAccumulatedWorldTransforms()
    {
        var root = Root(); var child = Child(root);
        var bad = new ScreenGeometry[] { new() { AnchorX = double.NaN }, new() { AnchorY = 2 }, new() { PivotX = -1 }, new() { PivotY = double.PositiveInfinity },
            new() { ScaleX = 0 }, new() { ScaleY = 1001 }, new() { Rotation = double.NegativeInfinity }, new() { Rotation = 360001 }, new() { Opacity = -.1 }, new() { Opacity = double.NaN } };
        foreach (var geometry in bad) Assert.Throws<ArgumentException>(() => new ScreenDefinition(Guid.NewGuid(), [root, child with { Geometry = geometry }]));
        foreach (var node in new[] { child with { Geometry = null! }, child with { Color = "red" }, child with { Order = int.MinValue } })
            Assert.Throws<ArgumentException>(() => new ScreenDefinition(Guid.NewGuid(), [root, node]));
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(Guid.NewGuid(), [root with { Geometry = new() { ScaleX = 1000 } }, child with { Geometry = new() { ScaleX = 1000 } }]));
        var panel = Child(root, ScreenNodeKind.Container) with { Geometry = new() { ScaleX = .001, ScaleY = .001 } };
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(Guid.NewGuid(), [root with { Geometry = panel.Geometry }, panel, Child(panel) with { Geometry = panel.Geometry }]));
    }

    [TestMethod]
    public async Task Version14MigratesWithoutWritingAndVersion15RequiresGeometryAndRejectsDowngrade()
    {
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("Сцена");
        var project = NovelProject.Create("Геометрия") with { Scenes = [scene] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["formatVersion"] = 14; json.AsObject().Remove("releaseCheck"); json.AsObject().Remove("startScreenSceneId"); File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            foreach (var node in json["scenes"]![0]!["canvas"]!["nodes"]!.AsArray())
                foreach (var key in new[] { "geometry", "clipChildren", "order", "color", "dialogue" }) node!.AsObject().Remove(key);
            File.WriteAllText(path, json.ToJsonString()); var bytes = File.ReadAllBytes(path);
            Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path))); CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            json["scenes"]![0]!["canvas"]!["nodes"]![0]!["kind"] = 5; File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            json["formatVersion"] = 15; json.AsObject().Remove("releaseCheck"); json.AsObject().Remove("startScreenSceneId"); File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            json["formatVersion"] = 14; json.AsObject().Remove("releaseCheck"); json.AsObject().Remove("startScreenSceneId"); json["scenes"]![0]!["canvas"]!["nodes"]![0] = null; File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }
}
