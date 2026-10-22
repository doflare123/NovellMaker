using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using NovellMaker.Testing;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GameSessionNativeTests
{
    [TestMethod]
    public async Task RepeatedMenuNewGameAndCancellationWorkInF6AndSelfContainedExport()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage5-session");
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
        var line = DialogueLine.Create("ru", "Новая игра с чистым состоянием");
        var story = NovelScene.Create("Сюжет") with { Lines = [line], ScriptBlocks = [
            new(null, ScriptPhase.SceneStart, new ScriptAction(Guid.NewGuid(), "game.set('fresh','yes')", "scenario.lua")),
            new(line.Id, ScriptPhase.DuringLine, new SetAction(Guid.NewGuid(), "seen", "game"))] };
        var menu = NovelScene.Create("Главное меню");
        var play = new ScreenNode(Guid.NewGuid(), menu.Canvas.Nodes[0].Id, ScreenNodeKind.Button, new(300, 200, 300, 50), "Новая игра");
        var status = new ScreenNode(Guid.NewGuid(), menu.Canvas.Nodes[0].Id, ScreenNodeKind.Text, new(300, 100, 500, 50), "Загрузка меню");
        var handler = new LuaScriptAsset(Guid.NewGuid(), "menu.lua", "local count=0; return {on_load=function() ui.set_text(refs.status,ui.get('seen') or 'Чистое меню') end, on_event=function() count=count+1; ui.set_text(refs.status,'Нажатий: '..count) end}");
        var check = new ScreenNode(Guid.NewGuid(), menu.Canvas.Nodes[0].Id, ScreenNodeKind.Button, new(300, 300, 300, 50), "Проверить Lua");
        menu = menu with { Lines = [DialogueLine.Create("ru", "НЕ ПОКАЗЫВАТЬ")],
            ScriptBlocks = [new(null, ScriptPhase.SceneStart, new ScriptAction(Guid.NewGuid(), "game.set('menu-ran','bad')", "forbidden.lua"))],
            Canvas = menu.Canvas with { Nodes = menu.Canvas.Nodes.Add(status).Add(play).Add(check), HandlerScriptId = handler.Id,
                References = ImmutableDictionary<string, Guid>.Empty.Add("status", status.Id), Buttons = [new(play.Id, ScreenButtonAction.NewGame, story.Id)] } };
        var project = AudioFixture.Attach(NovelProject.Create("Этап 5.1 · игровой сеанс") with { Scenes = [story, menu], Scripts = [handler],
            StartScreenSceneId = menu.Id, InputBindings = [new("F2", ScreenButtonAction.MainMenu, null), new("F3", ScreenButtonAction.NewGame, story.Id), new("F4", ScreenButtonAction.EndSession, null)],
            DialogueUi = new() { ShowNavigationControls = false } }, source);
        // A menu with audio bindings must still remain silent until an actual playthrough starts.
        project = project with { Audio = project.Audio with { SceneSounds = project.Audio.SceneSounds.Add(menu.Id, project.Audio.Assets[0].Id) } };
        var path = Path.Combine(source, "session.novell"); await new JsonProjectStore().SaveAsync(path, project);
        var plan = Path.Combine(directory, "plan.json");
        var steps = new List<object> { new { Kind = "tick" }, new { Kind = "next" }, new { Kind = "quicksave" } };
        for (var i = 0; i < 12; i++)
        {
            steps.Add(new { Kind = "click", X = 400, Y = 320 }); // Fresh Lua closure every return.
            steps.Add(new { Kind = "click", X = 400, Y = 220 }); // Project-owned NewGame button.
            steps.Add(new { Kind = "key", Key = "F3" }); // Duplicate must not reset the running game.
            steps.Add(new { Kind = "tick", Seconds = 2 });
            steps.Add(new { Kind = "key", Key = "F2" });
        }
        // Start and immediately cancel both kinds of pending work without waiting in the smoke harness.
        steps.Add(new { Kind = "key_now", Key = "F3" });
        steps.Add(new { Kind = "key_now", Key = "F2" });
        steps.Add(new { Kind = "key_now", Key = "F3" });
        steps.Add(new { Kind = "key_now", Key = "F2" });
        steps.Add(new { Kind = "tick" });
        File.WriteAllText(plan, JsonSerializer.Serialize(steps));
        foreach (var packaged in new[] { false, true })
        {
            var baseline = Process.GetProcessesByName("NovellMaker.Runtime").Select(p => { using (p) return p.Id; }).ToHashSet();
            var output = Path.Combine(directory, "game-" + Guid.NewGuid().ToString("N"));
            var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
            if (packaged)
            {
                await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, source, story.Id, output);
                runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
            }
            var label = packaged ? "export" : "preview"; var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var value in new[] { "--project", path, "--assets-root", source, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(value);
            else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            foreach (var value in new[] { "--smoke-output", Path.Combine(directory, label + ".png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Session smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            using var json = JsonDocument.Parse(File.ReadAllText(report)); var result = json.RootElement; var trace = result.GetProperty("ActionTrace");
            Assert.AreEqual(0, result.GetProperty("Diagnostics").GetArrayLength());
            Assert.AreEqual(0, result.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
            Assert.AreEqual("Menu", trace[0].GetProperty("SessionPhase").GetString());
            Assert.AreEqual(JsonValueKind.Null, trace[0].GetProperty("State").GetProperty("LineId").ValueKind);
            Assert.AreEqual(0, trace[0].GetProperty("AudioOpenedSources").GetInt32());
            Assert.IsFalse(trace[1].GetProperty("Success").GetBoolean()); Assert.IsFalse(trace[2].GetProperty("Success").GetBoolean());
            for (var i = 0; i < 12; i++)
            {
                var at = 3 + i * 5;
                Assert.IsTrue(trace[at].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Нажатий: 1"));
                Assert.AreEqual("Playing", trace[at + 1].GetProperty("SessionPhase").GetString());
                Assert.AreEqual(2 * i + 1, trace[at + 1].GetProperty("SessionGeneration").GetInt32());
                Assert.AreEqual(trace[at + 1].GetProperty("SessionGeneration").GetInt32(), trace[at + 2].GetProperty("SessionGeneration").GetInt32());
                Assert.AreEqual("yes", trace[at + 1].GetProperty("State").GetProperty("Variables").GetProperty("fresh").GetString());
                Assert.IsFalse(trace[at + 1].GetProperty("State").GetProperty("Variables").TryGetProperty("menu-ran", out _));
                Assert.AreEqual(0d, trace[at + 1].GetProperty("State").GetProperty("Time").GetDouble());
                Assert.IsGreaterThan(0, trace[at + 1].GetProperty("AudioSources").GetInt32());
                var returned = trace[at + 4]; Assert.AreEqual("Menu", returned.GetProperty("SessionPhase").GetString());
                Assert.AreEqual(0, returned.GetProperty("AudioSources").GetInt32()); Assert.AreEqual(1, returned.GetProperty("ScreenCount").GetInt32());
                Assert.AreEqual(0, returned.GetProperty("State").GetProperty("Variables").EnumerateObject().Count());
                Assert.AreEqual(0d, returned.GetProperty("State").GetProperty("Time").GetDouble());
                Assert.IsTrue(returned.GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Чистое меню"));
            }
            Assert.IsTrue(trace[63].GetProperty("PendingLua").GetBoolean());
            Assert.IsFalse(trace[64].GetProperty("PendingLua").GetBoolean());
            Assert.IsTrue(trace[64].GetProperty("PendingScreenLua").GetBoolean());
            Assert.AreEqual("Menu", trace[67].GetProperty("SessionPhase").GetString());
            Assert.AreEqual(0, trace[67].GetProperty("AudioSources").GetInt32());
            // DisposeAsync runs outside the frame loop and must leave no child runtime behind.
            var remaining = Process.GetProcessesByName("NovellMaker.Runtime").Where(p =>
            {
                if (!baseline.Contains(p.Id) && p.Id != process.Id && string.Equals(p.MainModule?.FileName, runtime, StringComparison.OrdinalIgnoreCase)) return true;
                p.Dispose(); return false;
            }).ToArray();
            try { Assert.IsEmpty(remaining, "A retired Lua worker survived runtime shutdown."); }
            finally { foreach (var child in remaining) child.Dispose(); }

            // Project-owned EndSession must also cancel work and shut down from a live new game.
            var exitPlan = Path.Combine(directory, "exit-plan.json");
            File.WriteAllText(exitPlan, "[{\"Kind\":\"key_now\",\"Key\":\"F3\"},{\"Kind\":\"key_now\",\"Key\":\"F4\"}]");
            start.ArgumentList[start.ArgumentList.Count - 1] = exitPlan;
            using var ended = Process.Start(start)!; var exitError = ended.StandardError.ReadToEndAsync(); var exitOutput = ended.StandardOutput.ReadToEndAsync();
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await ended.WaitForExitAsync(exitTimeout.Token); }
            catch (OperationCanceledException) { ended.Kill(true); await ended.WaitForExitAsync(); Assert.Fail("EndSession did not close the runtime."); }
            Assert.AreEqual(0, ended.ExitCode, await exitError + await exitOutput);
            var children = Process.GetProcessesByName("NovellMaker.Runtime").Where(p =>
            {
                if (!baseline.Contains(p.Id) && p.Id != ended.Id && string.Equals(p.MainModule?.FileName, runtime, StringComparison.OrdinalIgnoreCase)) return true;
                p.Dispose(); return false;
            }).ToArray();
            try { Assert.IsEmpty(children, "EndSession left a Lua worker running."); }
            finally { foreach (var child in children) child.Dispose(); }
            if (packaged && Path.GetDirectoryName(Path.GetFullPath(output)) == Path.GetFullPath(directory)) Directory.Delete(output, true);
        }
    }
}
