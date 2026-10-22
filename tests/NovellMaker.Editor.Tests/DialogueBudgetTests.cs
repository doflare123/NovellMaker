using Avalonia.Headless;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;
using NovellMaker.Editor.Views;
using NovellMaker.Rendering;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DialogueBudgetTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunAndExportsRejectLongTranslationBeforeCreatingFiles()
    {
        var line = NovellMaker.Core.Scenes.DialogueLine.Create("ru", "Короткая") with
        { Texts = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("ru", "Короткая").Add("en", new string('x', 8193)) };
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("Проверка") with { Lines = [line] };
        var project = NovellMaker.Core.Projects.NovelProject.Create("Проверка") with { Scenes = [scene] };
        var root = Path.Combine(Path.GetTempPath(), "DialogueLimit-" + Guid.NewGuid().ToString("N"));
        using var preview = new NovellMaker.Editor.Services.Preview.PreviewService("missing-runtime", root);
        var exporter = new NovellMaker.Editor.Services.Export.WindowsExportService("missing-template", new NovellMaker.Editor.Services.Assets.ImageAssetService());
        var installer = new NovellMaker.Editor.Services.Export.WindowsInstallerService(exporter, "missing-nsis");
        foreach (var run in new Func<Task>[] {
            () => preview.StartAsync(new(project, root, scene.Id, line.Id, "ru"), _ => { }),
            () => exporter.ExportAsync(project, root, scene.Id, Path.Combine(root, "game")),
            () => installer.ExportAsync(project, root, scene.Id, Path.Combine(root, "game.exe"), new()) })
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(run);
            StringAssert.Contains(error.Message, line.Id.ToString());
            StringAssert.Contains(error.Message, "en");
            StringAssert.Contains(error.Message, "8193");
            Assert.IsFalse(Directory.Exists(root));
        }
        var valid = project with { Scenes = [scene with { Lines = [line with { Texts = line.Texts.SetItem("en", new string('x', 8192)) }] }] };
        NovellMaker.Editor.Services.DialogueDisplayValidation.Validate(valid);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new NovellMaker.Core.Projects.JsonProjectStore();
            await store.SaveAsync(path, project);
            Assert.AreEqual(8193, (await store.LoadAsync(path)).Scenes[0].Lines[0].Texts["en"].Length);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task MeasureLargeCanvasSynchronousRasterization()
    {
        await using var headless = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        await headless.Dispatch(() =>
        {
            var style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
            var node = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Panel, new(0, 0, 1280, 720)) { Dialogue = DialogueBinding.Panel };
            var timings = new List<double>();
            for (var pass = 0; pass < 5; pass++)
            {
                var views = Enumerable.Range(0, 1023).Select(_ => new DialogueComponentView()).ToArray();
                try
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var view in views) view.Update(node, style, node.Bounds);
                    timings.Add(timer.Elapsed.TotalMilliseconds);
                    Assert.IsLessThanOrEqualTo(DialogueComponentView.MaximumBitmapBytes, DialogueComponentView.AllocatedBytes);
                }
                finally { foreach (var view in views) view.Clear(); }
            }
            TestContext.WriteLine("1023 panels 1280x720; synchronous Update ms: " + string.Join(", ", timings.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            return true;
        }, CancellationToken.None);
    }

    [TestMethod]
    public void OversizedTextIsBoundedBeforeParsingAndMeasurement()
    {
        var style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
        var text = new string('x', 1_000_000);
        var replacement = DialogueRasterizer.LayoutText(text);
        Assert.IsLessThan(100, replacement.Length);
        var expected = DialogueRasterizer.Measure(style, "", replacement);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Assert.AreEqual(expected, DialogueRasterizer.Measure(style, "", text));
        Assert.IsLessThan(1_000_000L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.AreEqual(8192, DialogueRasterizer.LayoutText(new string('x', 8192)).Length);
    }

    [TestMethod]
    public async Task BitmapBudgetIsSharedReleasedAndRejectedViewCanRetry()
    {
        await using var headless = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        await headless.Dispatch(() =>
        {
            var style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
            var node = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Panel, new(0, 0, 2048, 2048)) { Dialogue = DialogueBinding.Panel };
            var views = Enumerable.Range(0, 4).Select(_ => new DialogueComponentView()).ToArray();
            try
            {
                foreach (var view in views) view.Update(node, style, node.Bounds);
                Assert.AreEqual(DialogueComponentView.MaximumBitmapBytes, DialogueComponentView.AllocatedBytes);
                views[0].Clear();
                views[2].Update(node, style, node.Bounds);
                Assert.AreEqual(DialogueComponentView.MaximumBitmapBytes, DialogueComponentView.AllocatedBytes);
            }
            finally { foreach (var view in views) view.Clear(); }
            Assert.AreEqual(0L, DialogueComponentView.AllocatedBytes);
            return true;
        }, CancellationToken.None);
    }
}
