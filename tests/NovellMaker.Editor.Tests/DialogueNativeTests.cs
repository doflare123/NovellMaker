using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using NovellMaker.Testing;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DialogueNativeTests
{
    [TestMethod]
    [DataRow(true)] [DataRow(false)]
    public async Task DialoguePixelsWaitChoiceVoiceAndLoadMatchInF6AndStandalone(bool cssLayout)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable both NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage4", cssLayout ? "css" : "canvas");
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(Path.Combine(source, "Assets", "Images"));
        var spritePath = Path.Combine(source, "Assets", "Images", "transparent.png");
        using (var bitmap = new SKBitmap(1, 1)) { bitmap.Erase(SKColors.Transparent); using var file = File.Create(spritePath); bitmap.Encode(file, SKEncodedImageFormat.Png, 100); }
        var image = new ImageAsset(Guid.NewGuid(), "Прозрачный спрайт", "Assets/Images/transparent.png", 1, 1);
        var state = new CharacterState(Guid.NewGuid(), "Поза", image.Id); var speaker = NovelCharacter.Create("Анна · говорящий") with { States = [state], DefaultStateId = state.Id };
        var first = DialogueLine.Create("ru", "§#FF0000Красный фрагмент§r. " + string.Concat(Enumerable.Repeat("Текст с кириллицей и переносами. ", 10))) with { SpeakerId = speaker.Id };
        var second = DialogueLine.Create("ru", "Вторая фраза"); var scene = NovelScene.Create("Диалог на холсте");
        var css = ".dialogue { background-color:#18384E; color:#E6E7E9; border-color:#88CCEE; border-width:3px; border-radius:14px; font-size:28px; width:1050px; min-height:180px; left:100px; bottom:30px; padding:20px 24px; }";
        var style = DialogueCssParser.Parse(css);
        scene = scene with { Lines = [first, second], DialogueCssOverride = css,
            ScriptBlocks = [new(first.Id, ScriptPhase.DuringLine, new WaitAction(Guid.NewGuid(), "timer", .5)),
                new(first.Id, ScriptPhase.AfterLine, new ChoiceAction(Guid.NewGuid(), [new(Guid.NewGuid(), "Продолжить к другой фразе", scene.Id, ScenarioProgram.LineEntryId(second.Id))]))],
            Canvas = scene.Canvas with { Nodes = DialogueCanvas.Add(scene.Canvas.Nodes, scene.Id, style, legacy: true) } };
        if (!cssLayout)
        {
            var nodes = scene.Canvas.Nodes; var panel = nodes[1];
            nodes = nodes.SetItem(1, panel with { Dialogue = DialogueBinding.Panel, Bounds = new(200, 330, 800, 240), Geometry = new() { PivotX = .5, PivotY = .5, Rotation = -7, Opacity = .9 } });
            nodes = nodes.SetItem(2, nodes[2] with { Bounds = new(420, 14, 350, 38), Order = 4 });
            nodes = nodes.SetItem(3, nodes[3] with { Bounds = new(28, 65, 700, 150) });
            // The child extends past its transformed parent and must use the same clipping in both hosts.
            nodes = nodes.Add(new(Guid.NewGuid(), panel.Id, ScreenNodeKind.Panel, new(760, 200, 200, 120)) { Color = "#FF8080", Order = -1 });
            scene = scene with { Canvas = scene.Canvas with { Nodes = nodes } };
        }
        var button = new ScreenNode(Guid.NewGuid(), scene.Canvas.Nodes[1].Id, ScreenNodeKind.Button,
            cssLayout ? new(800, 200, 200, 44) : new(600, 180, 180, 44), "Нажать в диалоге") { Order = 10 };
        var status = new ScreenNode(Guid.NewGuid(), scene.Canvas.Nodes[0].Id, ScreenNodeKind.Text, new(30, 30, 360, 50), "До нажатия");
        var handler = new LuaScriptAsset(Guid.NewGuid(), "dialogue-button.lua", "return {on_event=function(event) if event.node==refs.button then ui.set_text(refs.status,'Нажато') end end}");
        scene = scene with { Canvas = scene.Canvas with { Nodes = scene.Canvas.Nodes.Add(button).Add(status), HandlerScriptId = handler.Id,
            References = ImmutableDictionary<string, Guid>.Empty.Add("button", button.Id).Add("status", status.Id) } };
        if (cssLayout) Assert.IsFalse(scene.AsScreen().Definition().Buttons.Any(n => n.Id == button.Id), "This button only becomes visible after CSS auto layout.");
        var metrics = NovellMaker.Rendering.DialogueRasterizer.Measure(style, speaker.Name, first.Texts["ru"]);
        var presentation = DialogueCanvas.Present(scene.AsScreen().Definition(), style, speaker.Name, first.Texts["ru"], true, metrics.Height, metrics.SpeakerHeight);
        var point = presentation.Visual(button.Id).World.Apply(40, 20);
        var project = AudioFixture.Attach(NovelProject.Create("Этап 4") with { Scenes = [scene], Characters = [speaker], Images = [image], Scripts = [handler], DialogueUi = new() { ShowNavigationControls = false } }, source);
        var path = Path.Combine(source, "dialogue.novell"); await new JsonProjectStore().SaveAsync(path, project);
        var plan = Path.Combine(directory, "plan.json"); File.WriteAllText(plan, """
            [{"Kind":"tick"},{"Kind":"next"},{"Kind":"tick","Seconds":0.5},{"Kind":"save"},
             {"Kind":"next"},{"Kind":"next"},{"Kind":"click","X":500,"Y":160},
             {"Kind":"load"},{"Kind":"tick"},{"Kind":"tick"},{"Kind":"tick"},{"Kind":"tick"},
            """ + JsonSerializer.Serialize(new { Kind = "click", X = (int)point.X, Y = (int)point.Y }) + ",{\"Kind\":\"tick\"},{\"Kind\":\"tick\"},{\"Kind\":\"tick\"}]");
        byte[]? previewPixels = null;
        foreach (var packaged in new[] { false, true })
        {
            var output = Path.Combine(directory, "game-" + Guid.NewGuid().ToString("N"));
            string runtime;
            if (packaged)
            {
                await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, source, scene.Id, output);
                runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
            }
            else runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
            var label = packaged ? "export" : "preview"; var screenshot = Path.Combine(directory, label + ".png"); var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var argument in new[] { "--project", path, "--assets-root", source, "--scene", scene.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(argument);
            else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            foreach (var argument in new[] { "--smoke-output", screenshot, "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Dialogue smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            using var json = JsonDocument.Parse(File.ReadAllText(report)); var result = json.RootElement; var trace = result.GetProperty("ActionTrace");
            Assert.AreEqual(0, result.GetProperty("Diagnostics").GetArrayLength()); Assert.AreEqual(0, result.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
            Assert.AreEqual("timer", trace[0].GetProperty("State").GetProperty("Wait").GetProperty("Mode").GetString());
            Assert.AreEqual(3, trace[0].GetProperty("AudioOpenedSources").GetInt32()); Assert.IsFalse(trace[1].GetProperty("Success").GetBoolean());
            Assert.AreEqual("input", trace[2].GetProperty("State").GetProperty("Wait").GetProperty("Mode").GetString());
            Assert.AreEqual(JsonValueKind.Object, trace[4].GetProperty("State").GetProperty("Choice").ValueKind); Assert.IsFalse(trace[5].GetProperty("Success").GetBoolean());
            Assert.AreEqual(second.Id, trace[6].GetProperty("State").GetProperty("LineId").GetGuid());
            Assert.AreEqual(first.Id, trace[7].GetProperty("State").GetProperty("LineId").GetGuid());
            Assert.AreEqual(4, trace[7].GetProperty("AudioOpenedSources").GetInt32(), "Load restarts sound without replaying voice/effect.");
            Assert.IsTrue(trace[12].GetProperty("Success").GetBoolean());
            Assert.IsTrue(trace[12].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Нажато"));
            using var frame = SKBitmap.Decode(screenshot); var pixels = frame.Bytes;
            var corner = presentation.Visual(button.Id).World.Apply(button.Bounds.Width - 5, button.Bounds.Height - 5);
            Assert.IsGreaterThan((byte)40, frame.GetPixel((int)corner.X, (int)corner.Y).Blue, "A dynamically visible, enabled button must use its active surface, not a disabled tint from authoring bounds.");
            Assert.IsTrue(frame.Pixels.Count(p => p.Red > 180 && p.Green < 80 && p.Blue < 80) > 20, "Inline red text must reach the shared canvas renderer.");
            Assert.AreNotEqual(new SKColor(18, 20, 24), frame.GetPixel(cssLayout ? 110 : 250, cssLayout ? 650 : 400));
            if (packaged) CollectionAssert.AreEqual(previewPixels!, pixels, "F6 and standalone must draw identical logical pixels."); else previewPixels = pixels;
            if (packaged && Path.GetDirectoryName(Path.GetFullPath(output)) == directory) Directory.Delete(output, true);
        }
    }
}
