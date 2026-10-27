using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AnimationsNativeTests
{
    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task TweenGroupsCancellationAndCompletionRenderInPreviewAndExport(bool packaged, bool minigame)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage9-animations", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Анимации компонентов")] };
        var project = ExampleAnimationsModule.AddTo(NovelProject.Create("Анимации") with { Scenes = [story] });
        if (minigame)
        {
            var game = project.Scenes.Last();
            story = story with { ScriptBlocks = [new(null, ScriptPhase.SceneStart, new StartMinigameAction(Guid.NewGuid(), game.Id, LuaValue.FromObject([])))] };
            project = project with { Scenes = project.Scenes.SetItem(0, story).SetItem(1, game with { Canvas = game.Canvas with { Minigame = new MinigameDefinition() } }),
                Scripts = project.Scripts.SetItem(project.Scripts.Length - 1, project.Scripts.Last() with { Source = """
                    return {on_load=function()
                      if ui.is_restoring() then ui.set_geometry(refs.marker,{x=100})
                      else ui.animate(ui.tween(refs.marker,{x=100},1)) end
                    end,on_event=function(e)
                      if e.kind=='animation_result' then ui.set_text(refs.state,e.status) end
                    end}
                    """ }) };
        }
        var file = Path.Combine(directory, "animations.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        var plan = Path.Combine(directory, "animation.plan.json"); File.WriteAllText(plan, minigame
            ? "[{\"Kind\":\"tick\"}," + string.Join(",", Enumerable.Repeat("{\"Kind\":\"tick\",\"Seconds\":0.2}", 10)) + ",{\"Kind\":\"save\"}]"
            : """
            [{"Kind":"key","Key":"F11"},{"Kind":"click","X":350,"Y":490},
             {"Kind":"tick","Seconds":0.5},{"Kind":"click","X":850,"Y":490},
             {"Kind":"tick","Seconds":5},{"Kind":"click","X":350,"Y":490},
             {"Kind":"tick","Seconds":1.5},{"Kind":"tick","Seconds":0.5},
             {"Kind":"click","X":350,"Y":490},{"Kind":"click","X":350,"Y":490},
             {"Kind":"back"},{"Kind":"key","Key":"F11"}]
            """);
        var report = Path.Combine(directory, "animation.json");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var arg in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
        foreach (var arg in new[] { "--smoke-output", Path.Combine(directory, "animation.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Animation native smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var trace = json.RootElement.GetProperty("ActionTrace");
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
        Assert.IsTrue(trace.EnumerateArray().All(t => t.GetProperty("Success").GetBoolean()));
        JsonElement Marker(int step) => trace[step].GetProperty("ScreenNodes").EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "marker");
        double X(int step) => Marker(step).GetProperty("Bounds").GetProperty("X").GetDouble();
        if (minigame)
        {
            Assert.AreEqual(200, X(0)); Assert.AreEqual(180, X(1), .001); Assert.AreEqual(100, X(10));
            Assert.AreEqual(0, trace[10].GetProperty("ActiveAnimations").GetInt32());
            Assert.IsTrue(trace[10].GetProperty("ScreenTexts").EnumerateArray().Any(n => n.GetString() == "completed"));
            Assert.IsTrue(File.Exists(Path.ChangeExtension(report, ".save.json"))); return;
        }
        Assert.AreEqual(550, X(2), .001); Assert.AreEqual(X(2), X(4)); Assert.AreEqual(550, X(6), .001);
        Assert.AreEqual(200, X(7)); Assert.AreEqual(0, Marker(7).GetProperty("Geometry").GetProperty("Rotation").GetDouble());
        Assert.AreEqual(1, Marker(7).GetProperty("Geometry").GetProperty("Opacity").GetDouble());
        Assert.IsTrue(trace[3].GetProperty("ScreenTexts").EnumerateArray().Any(n => n.GetString() == "Отменено"));
        Assert.IsTrue(trace[7].GetProperty("ScreenTexts").EnumerateArray().Any(n => n.GetString() == "Завершено"));
        Assert.AreEqual(1, trace[9].GetProperty("ActiveAnimations").GetInt32()); Assert.AreEqual(0, trace[10].GetProperty("ActiveAnimations").GetInt32());
        Assert.AreEqual(200, X(11));
    }
}
