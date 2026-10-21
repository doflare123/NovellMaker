using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ScreenTextureCacheTests
{
    private static readonly ScreenDefinition Fixture = ScreenFixture.Create("Тест").Definition();
    [TestMethod]
    public void SameSceneOpenedTwiceUsesIndependentTexturesAndOneSharedAllocationBudget()
    {
        var scene = ScreenFixture.Create("Два экземпляра"); var stack = new ScreenStack(Guid.NewGuid());
        stack.Open(Guid.NewGuid(), scene.Definition()); stack.Open(Guid.NewGuid(), scene.Definition());
        using var cache = new ScreenTextureCache<Texture>(_ => new Texture());
        var first = cache.UpdateLayers(stack.Instances, new(0, 0, 1280, 720));
        Assert.IsLessThanOrEqualTo(ScreenTextureCache<Texture>.MaximumFrameTextures, first.Count);
        var completed = cache.UpdateLayers(stack.Instances, new(0, 0, 1280, 720));
        Assert.HasCount(6, completed); Assert.HasCount(2, completed.Select(t => t.Region.InstanceId).Distinct().ToArray());
        var title = completed.Where(t => t.Region.Node.Id == scene.Nodes[1].Id).ToArray();
        Assert.HasCount(2, title); Assert.AreNotSame(title[0].Texture, title[1].Texture);
        stack.Close(stack.Instances.Last()); cache.UpdateLayers(stack.Instances, new(0, 0, 1280, 720));
        Assert.IsLessThanOrEqualTo(ScreenTextureCache<Texture>.MaximumCachedPixels, cache.CachedPixels);
    }
    private sealed class Texture : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
    private static readonly ScreenRect Viewport = new(0, 0, 1280, 720);

    [TestMethod]
    public void TextChangeOnlyReplacesItsNodeAndVisibilityOrClosureReleasesTextures()
    {
        var created = new List<Texture>();
        using var cache = new ScreenTextureCache<Texture>(_ => { var value = new Texture(); created.Add(value); return value; });
        var definition = Fixture;
        Assert.AreEqual(3, cache.Update(definition, Viewport).Count);
        cache.Update(new(definition.Id, definition.Nodes), Viewport);
        Assert.AreEqual(3, created.Count);
        definition = new(definition.Id, [.. definition.Nodes.Select((n, i) => i == 1 ? n with { Text = "new title" } : n)]);
        cache.Update(definition, Viewport);
        Assert.AreEqual(4, created.Count); Assert.AreEqual(1, created[0].Disposals);
        Assert.AreEqual(0, created[1].Disposals); Assert.AreEqual(0, created[2].Disposals);
        definition = new(definition.Id, [.. definition.Nodes.Select(n => n with { Enabled = false })]);
        cache.Update(definition, Viewport); Assert.AreEqual(4, created.Count);
        definition = new(definition.Id, [.. definition.Nodes.Select((n, i) => i == 0 ? n with { Visible = false } : n)]);
        Assert.AreEqual(0, cache.Update(definition, Viewport).Count);
        Assert.IsTrue(created.All(t => t.Disposals == 1)); Assert.AreEqual(0, cache.CachedPixels);
        cache.Update(null, Viewport); cache.Dispose();
        Assert.IsTrue(created.All(t => t.Disposals == 1));
    }

    [TestMethod]
    public void HugeOffscreenOrNestedClippedNodesNeverAllocateTheirLogicalSurface()
    {
        var root = Guid.NewGuid(); var panel = Guid.NewGuid(); var text = Guid.NewGuid();
        var definition = new ScreenDefinition(Guid.NewGuid(), [
            new(root, null, ScreenNodeKind.Container, new(10, 20, 200, 200)),
            new(panel, root, ScreenNodeKind.Panel, new(30, 40, 100, 90)),
            new(text, panel, ScreenNodeKind.Text, new(-50, -60, 100000, 100000), "clipped"),
            new(Guid.NewGuid(), root, ScreenNodeKind.Button, new(5000, 5000, 100000, 100000))]);
        var plan = ScreenTextureCache<Texture>.Plan(definition, Viewport);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual(40, plan[0].X); Assert.AreEqual(60, plan[0].Y);
        Assert.AreEqual(new ScreenRaster("clipped", 100, 90, 50, 60, 100000, Transparent: true), plan[0].Raster);
        using var cache = new ScreenTextureCache<Texture>(_ => new());
        cache.Update(definition, Viewport); Assert.AreEqual(9000, cache.CachedPixels);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DialogueTextureRenderer.RenderButton(null!, "huge", 100000, 100000));
    }

    [TestMethod]
    [DataRow(100, 100, 4)][DataRow(512, 256, 2)]
    public void AllocationWorkIsBoundedPerFrameAndDeferredNodesEventuallyRender(int width, int height, int firstFrame)
    {
        var root = Guid.NewGuid();
        var definition = new ScreenDefinition(Guid.NewGuid(), [
            new(root, null, ScreenNodeKind.Container, Viewport),
            .. Enumerable.Range(0, 10).Select(_ => new ScreenNode(Guid.NewGuid(), root, ScreenNodeKind.Text, new(0, 0, width, height)))]);
        var count = 0;
        using var cache = new ScreenTextureCache<Texture>(_ => { count++; return new(); });
        Assert.AreEqual(firstFrame, cache.Update(definition, Viewport).Count); Assert.AreEqual(firstFrame, count);
        for (var i = 0; i < 4; i++) cache.Update(definition, Viewport);
        Assert.AreEqual(10, count); Assert.AreEqual(10 * width * height, cache.CachedPixels);
        cache.Update(definition, Viewport); Assert.AreEqual(10, count);
    }

    [TestMethod]
    public void PerTextureAndTotalPixelLimitsAreIndependentOfNodeCount()
    {
        var root = Guid.NewGuid();
        var definition = new ScreenDefinition(Guid.NewGuid(), [new(root, null, ScreenNodeKind.Container, new(0, 0, 100000, 100000)),
            new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(0, 0, 2049, 1)),
            new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(0, 0, 1, 2049)),
            new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(0, 0, 1024, 1024)),
            .. Enumerable.Range(0, 20).Select(_ => new ScreenNode(Guid.NewGuid(), root, ScreenNodeKind.Text, new(0, 0, 512, 512)))]);
        var count = 0;
        using var cache = new ScreenTextureCache<Texture>(_ => { count++; return new(); });
        for (var i = 0; i < 25; i++) cache.Update(definition, new(0, 0, 100000, 100000));
        Assert.AreEqual(16, count); Assert.AreEqual(ScreenTextureCache<Texture>.MaximumCachedPixels, cache.CachedPixels);
        Assert.AreEqual(3, cache.RejectedNodes); Assert.AreEqual(4, cache.DeferredNodes);
    }
}
