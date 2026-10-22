using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GameServicesNativeTests
{
    [TestMethod]
    public async Task AuthorMenuPagesSaveLoadDeleteAndContinueThroughRealLuaInF6AndExport()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage5-services");
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
        var story = NovelScene.Create("Новая глава") with { Lines = [DialogueLine.Create("ru", "Начало"), DialogueLine.Create("ru", "Восстановленная фраза")] };
        var project = ExampleGameMenusModule.AddTo(NovelProject.Create("Авторские сохранения") with { Scenes = [story] }, story.Id);
        var path = Path.Combine(source, "services.novell"); await new JsonProjectStore().SaveAsync(path, project);
        var plan = Path.Combine(directory, "plan.json");
        File.WriteAllText(plan, """
            [{"Kind":"tick"},{"Kind":"click","X":630,"Y":390},
             {"Kind":"click","X":345,"Y":570},{"Kind":"click","X":120,"Y":570},
             {"Kind":"click","X":1050,"Y":160},{"Kind":"click","X":900,"Y":160},
             {"Kind":"key","Key":"F10"},{"Kind":"click","X":570,"Y":570},
             {"Kind":"click","X":1070,"Y":570},{"Kind":"key","Key":"F2"},
             {"Kind":"click","X":630,"Y":310},{"Kind":"key","Key":"F10"},
             {"Kind":"click","X":1050,"Y":160},{"Kind":"back"},{"Kind":"key","Key":"F2"},
             {"Kind":"click","X":630,"Y":230},{"Kind":"key","Key":"F10"}]
            """);
        using var seed = new GameSession(project, story.Id, "ru"); seed.NewGame(0); seed.Player.Next(); seed.Player.Tick(TimeSpan.FromSeconds(6));
        var saved = GameSlotStore.Capture(seed);
        foreach (var packaged in new[] { false, true })
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Path.Combine(local, "NovellMaker", packaged ? "Games" : "Saves", project.Id.ToString());
            var quick = packaged ? Path.Combine(profile, "quicksave.json") : Path.Combine(local, "NovellMaker", "Saves", project.Id + ".json");
            var store = new GameSlotStore(project, Path.Combine(profile, "slots"), quick);
            for (var i = 0; i < 8; i++) await store.SaveAsync("slot-" + i, saved with { Metadata = saved.Metadata with { Title = "Слот " + i, SavedAtUtc = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(i) } });
            await store.SaveAsync("broken", saved);
            var brokenPath = Path.Combine(profile, "slots", "broken.json");
            var broken = JsonNode.Parse(File.ReadAllText(brokenPath))!;
            var brokenSave = JsonNode.Parse(saved.SaveJson)!;
            brokenSave["State"]!["LineId"] = Guid.Empty; broken["Metadata"]!["LineId"] = Guid.Empty;
            broken["SaveJson"] = brokenSave.ToJsonString(); File.WriteAllText(brokenPath, broken.ToJsonString());
            var output = Path.Combine(directory, "export-" + Guid.NewGuid().ToString("N"));
            var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
            if (packaged)
            {
                await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, source, story.Id, output);
                runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
                var manifest = GamePackage.Load(output); Assert.AreEqual(2, manifest.Version); Assert.AreEqual(project.StartScreenSceneId, manifest.StartScreenSceneId);
                Assert.AreEqual(2, manifest.SaveContainerVersion); manifest.ValidateProject(project);
            }
            var label = packaged ? "export" : "preview"; var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var value in new[] { "--project", path, "--assets-root", source, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(value);
            else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            foreach (var value in new[] { "--smoke-output", Path.Combine(directory, label + ".png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Game service smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            using var json = JsonDocument.Parse(File.ReadAllText(report)); var result = json.RootElement; var trace = result.GetProperty("ActionTrace");
            var diagnostics = string.Join("\n", result.GetProperty("Diagnostics").EnumerateArray().Select(e => e.GetString()));
            Assert.AreEqual(0, result.GetProperty("Diagnostics").GetArrayLength(), diagnostics);
            Assert.AreEqual(0, result.GetProperty("ScreenLuaDiagnostics").GetArrayLength(), diagnostics);
            Assert.IsTrue(trace.EnumerateArray().All(s => s.GetProperty("Success").GetBoolean()));
            bool Text(int index, string expected) => trace[index].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == expected);
            Assert.IsTrue(Text(1, "Сохранений: 9 · страница 1")); Assert.IsTrue(Text(2, "Сохранений: 9 · страница 2"));
            Assert.IsTrue(Text(4, "Сохранений: 8 · страница 1")); Assert.IsTrue(Text(7, "Сохранений: 9 · страница 1"));
            foreach (var i in new[] { 5, 10 })
            { Assert.AreEqual(story.Lines[1].Id, trace[i].GetProperty("State").GetProperty("LineId").GetGuid()); Assert.AreEqual(6d, trace[i].GetProperty("State").GetProperty("Time").GetDouble()); }
            Assert.AreEqual(story.Lines[0].Id, trace[15].GetProperty("State").GetProperty("LineId").GetGuid()); Assert.AreEqual(0d, trace[15].GetProperty("State").GetProperty("Time").GetDouble());
            Assert.IsGreaterThan(0, result.GetProperty("PendingSlotDrawFrames").GetInt32()); Assert.IsGreaterThan(0, result.GetProperty("PendingScreenLuaDrawFrames").GetInt32());
            Assert.IsFalse(File.Exists(Path.Combine(profile, "slots", "slot-7.json")));
            Assert.HasCount(8, await store.ListAsync());
            foreach (var slot in await store.ListAsync()) await store.DeleteAsync(slot.Id);
        }
    }
}
