using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VisualObjectsNativeTests
{
    [TestMethod]
    [DataRow(false, 960, 540)] [DataRow(true, 900, 900)]
    public async Task GeometryImagesInputLiveLuaAndLoadRunInPreviewAndStandalone(bool packaged, int width, int height)
    {
        if (Environment.GetEnvironmentVariable(packaged ? "NOVELLMAKER_EXPORT_SMOKE" : "NOVELLMAKER_NATIVE_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime/WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage3", packaged ? "export" : "preview"); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(Path.Combine(source, "Assets", "Images"));
        using (var bitmap = new SKBitmap(32, 32))
        { bitmap.Erase(new SKColor(200, 100, 50)); using var stream = File.Create(Path.Combine(source, "Assets", "Images", "цвет.png")); bitmap.Encode(stream, SKEncodedImageFormat.Png, 100); }
        var image = new ImageAsset(Guid.NewGuid(), "Настоящий PNG", "Assets/Images/цвет.png", 32, 32);
        var scene = NovelScene.Create("Геометрия · кириллица"); var root = scene.Canvas.Nodes[0];
        var panel = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Panel, new(300, 180, 300, 200)) { Geometry = new() { PivotX = .5, PivotY = .5, Rotation = 30 } };
        var sprite = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Image, new(200, 80, 200, 140)) { ImageId = image.Id, Geometry = new() { Opacity = .5 }, Color = "#80FFFF" };
        var hit = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Area, new(200, 80, 200, 140));
        var label = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(60, 50, 500, 60), "Ожидание щелчка");
        var badge = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(750, 200, 360, 60), "Шаблон", Visible: false) { Geometry = new() { Rotation = -12 }, Color = "#80CCFF" };
        var script = new LuaScriptAsset(Guid.NewGuid(), "visual-objects.lua", """
            local count=0
            local copy
            return {
              on_load=function() copy=ui.create(refs.badge,refs.root);ui.set_visible(copy,true);ui.set_text(copy,'Живой экземпляр: 0') end,
              on_event=function(event)
                if event.node==refs.hit then
                  count=count+1
                  ui.remove(copy)
                  copy=ui.create(refs.badge,refs.root)
                  ui.set_visible(copy,true)
                  ui.set_geometry(copy,{anchor_x=0,pivot_x=0.5,scale_x=1.1,opacity=0.8,clip_children=false})
                  ui.set_text(copy,'Живой экземпляр: '..count)
                  ui.set_text(refs.label,'Нажатий: '..count)
                end
              end
            }
            """);
        scene = scene with { Canvas = scene.Canvas with { Nodes = [root, panel, sprite, hit, label, badge], HandlerScriptId = script.Id,
            References = ImmutableDictionary<string, Guid>.Empty.Add("root", root.Id).Add("badge", badge.Id).Add("hit", hit.Id).Add("label", label.Id) } };
        var project = NovelProject.Create("Проверка общих объектов") with { Scenes = [scene], Images = [image], Scripts = [script] };
        var path = Path.Combine(source, "test.novell"); await new JsonProjectStore().SaveAsync(path, project);
        var point = scene.AsScreen().Definition().Visual(panel.Id).World.Apply(250, 120);
        var clipped = scene.AsScreen().Definition().Visual(panel.Id).World.Apply(350, 120);
        int MapX(double x) => (int)Math.Round(x * Math.Min(width / 1280d, height / 720d) + (width - 1280 * Math.Min(width / 1280d, height / 720d)) / 2);
        int MapY(double y) => (int)Math.Round(y * Math.Min(width / 1280d, height / 720d) + (height - 720 * Math.Min(width / 1280d, height / 720d)) / 2);
        var steps = new object[] { new { Kind = "resize", X = width, Y = height },
            new { Kind = "window_click", X = MapX(clipped.X), Y = MapY(clipped.Y) },
            new { Kind = "window_click", X = MapX(point.X), Y = MapY(point.Y) }, new { Kind = "quicksave" }, new { Kind = "quickload" },
            new { Kind = "window_click", X = MapX(point.X), Y = MapY(point.Y) }, new { Kind = "window_click", X = MapX(point.X), Y = MapY(point.Y) } };
        var plan = Path.Combine(directory, "plan.json"); File.WriteAllText(plan, JsonSerializer.Serialize(steps));
        var report = Path.Combine(directory, "report.json"); var screenshot = Path.Combine(directory, "frame.png");
        string runtime; string? gameDirectory = null;
        if (packaged)
        {
            var output = Path.Combine(directory, "game-" + Guid.NewGuid().ToString("N"));
            gameDirectory = output;
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, source, scene.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        else runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var argument in new[] { "--project", path, "--assets-root", source, "--scene", scene.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(argument);
        else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
        foreach (var argument in new[] { "--smoke-output", screenshot, "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Visual object smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var result = json.RootElement;
        Assert.AreEqual(width, result.GetProperty("WindowWidth").GetInt32()); Assert.AreEqual(height, result.GetProperty("WindowHeight").GetInt32());
        Assert.AreEqual(0, result.GetProperty("Diagnostics").GetArrayLength()); Assert.AreEqual(0, result.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
        var trace = result.GetProperty("ActionTrace"); Assert.AreEqual(7, trace.GetArrayLength());
        Assert.IsFalse(trace[1].GetProperty("Success").GetBoolean(), "Clipped part must not receive a click.");
        Assert.IsTrue(trace[2].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Нажатий: 1"));
        Assert.IsTrue(trace[4].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Ожидание щелчка"), "Load must not execute on_load.");
        Assert.IsFalse(trace[4].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString()!.StartsWith("Живой экземпляр")));
        Assert.IsTrue(trace[6].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Нажатий: 2"));
        Assert.IsTrue(trace[6].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Живой экземпляр: 2"));
        using var frame = SKBitmap.Decode(screenshot); var pixel = frame.GetPixel((int)point.X, (int)point.Y);
        Assert.AreEqual(60, pixel.Red, 2); Assert.AreEqual(61, pixel.Green, 2); Assert.AreEqual(38, pixel.Blue, 2);
        using var windowFrame = SKBitmap.Decode(Path.ChangeExtension(screenshot, ".window.png")); Assert.AreEqual(width, windowFrame.Width); Assert.AreEqual(height, windowFrame.Height);
        File.Delete(result.GetProperty("SavePath").GetString()!);
        if (gameDirectory is not null && Path.GetDirectoryName(Path.GetFullPath(gameDirectory)) == directory) Directory.Delete(gameDirectory, true);
    }
}
