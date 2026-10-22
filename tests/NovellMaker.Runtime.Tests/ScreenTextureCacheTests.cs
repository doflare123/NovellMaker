using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ScreenTextureCacheTests
{
    [TestMethod]
    public void LargeDialogueTilesShareFrameAndCacheBudgetsAndTextOnlyUpdatesKeepPanelRaster()
    {
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("Сцена"); var root = scene.Canvas.Nodes[0];
        var panel = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Panel, new(0, 0, 1280, 720)) { Dialogue = DialogueBinding.Panel };
        var text = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Text, new(20, 20, 1200, 680), "Реплика") { Dialogue = DialogueBinding.Text };
        var stack = new ScreenStack(Guid.NewGuid()); var instance = stack.Open(Guid.NewGuid(), new(scene.Id, [root, panel, text]));
        var style = NovellMaker.Core.Styling.DialogueCssParser.Parse(NovellMaker.Core.Styling.DialogueCssParser.DefaultCss);
        var allocations = 0; using var cache = new ScreenTextureCache<Texture>(_ => { allocations++; return new(); });
        var plan = ScreenTextureCache<Texture>.Plan(instance.Definition, Viewport);
        Assert.IsTrue(plan.All(n => n.Raster.Width * n.Raster.Height <= ScreenTextureCache<Texture>.MaximumTexturePixels));
        Assert.AreEqual(1280 * 720 + 1200 * 680, plan.Sum(n => n.Raster.Width * n.Raster.Height));
        for (var frame = 0; frame < 12; frame++) cache.UpdateLayers(stack.Instances, Viewport, style: style);
        Assert.AreEqual(0, cache.RejectedNodes); Assert.AreEqual(0, cache.DeferredNodes); Assert.AreEqual(plan.Count, allocations);
        var count = allocations;
        var changed = new ScreenDefinition(scene.Id, [root, panel, text with { Text = "Другая реплика" }]);
        for (var frame = 0; frame < 6; frame++) cache.UpdateLayers(stack.Instances, Viewport, _ => changed, style);
        Assert.AreEqual(count + plan.Count(n => n.Node.Id == text.Id), allocations);
        cache.Update(null, Viewport); Assert.AreEqual(0, cache.CachedPixels);
    }

    private static readonly ScreenDefinition Fixture = ScreenFixture.Create("Тест").Definition();
    [TestMethod]
    public void ReducedOversizedButtonReportsRejectedRasterWithoutAllocatingAndDiagnosticsClearOnRecovery()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, Viewport);
        var button = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Button, new(100, 200, 3000, 500), "Большая кнопка")
            { Name = "Уменьшенная кнопка", Geometry = new() { ScaleX = .1, ScaleY = .1 } };
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, button]); var allocations = 0;
        using var cache = new ScreenTextureCache<Texture>(_ => { allocations++; return new(); });
        Assert.IsEmpty(cache.Update(definition, Viewport)); Assert.AreEqual(0, allocations); Assert.AreEqual(0, cache.CachedPixels);
        Assert.AreEqual(1, cache.RejectedNodes); var diagnostic = cache.Diagnostics.Single();
        StringAssert.Contains(diagnostic, button.Name); StringAssert.Contains(diagnostic, button.Id.ToString()); StringAssert.Contains(diagnostic, "бюджет");
        cache.Update(definition, Viewport); Assert.AreEqual(diagnostic, cache.Diagnostics.Single());
        Assert.AreEqual(button.Id, definition.HitTest(150, 220), "The renderer must draw a visible allocation-free button surface here.");
        definition = new(definition.Id, [root, button with { Bounds = new(100, 200, 300, 50), Geometry = new() }]);
        Assert.HasCount(1, cache.Update(definition, Viewport)); Assert.AreEqual(1, allocations); Assert.IsEmpty(cache.Diagnostics);
        cache.Update(null, Viewport); Assert.IsEmpty(cache.Diagnostics); Assert.AreEqual(0, cache.CachedPixels);
    }

    [TestMethod]
    public void FrameDeferralIsTemporaryAndCapacityDeferralReportsAndRecoversAfterRelease()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, Viewport);
        var definition = new ScreenDefinition(Guid.NewGuid(), [root, .. Enumerable.Range(0, 17).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Button, new(0, 0, 512, 512)))]);
        using var cache = new ScreenTextureCache<Texture>(_ => new());
        cache.Update(definition, Viewport); Assert.AreEqual(16, cache.DeferredNodes); Assert.AreEqual(0, cache.CapacityDeferredNodes); Assert.IsEmpty(cache.Diagnostics);
        for (var i = 0; i < 15; i++) cache.Update(definition, Viewport);
        Assert.AreEqual(ScreenTextureCache<Texture>.MaximumCachedPixels, cache.CachedPixels);
        Assert.AreEqual(1, cache.CapacityDeferredNodes); Assert.AreEqual(1, cache.DeferredNodes); StringAssert.Contains(cache.Diagnostics.Single(), "кэш");
        var last = definition.Nodes[^1];
        cache.Update(new(definition.Id, [root, last]), Viewport); Assert.IsEmpty(cache.Diagnostics); Assert.AreEqual(0, cache.DeferredNodes);
        cache.Dispose(); Assert.AreEqual(0, cache.CachedPixels); Assert.IsEmpty(cache.Diagnostics);
    }
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
