using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GameSlotsNativeTests
{
    [TestMethod]
    public async Task AsyncQuickslotLegacyLoadListAndLatestWorkInPreviewAndAutonomousGame()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage5-slots");
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Начало"), DialogueLine.Create("ru", "Поздний слот")] };
        var menu = NovelScene.Create("Авторское меню");
        var project = NovelProject.Create("Этап 5.2 · слоты") with { Scenes = [story, menu], StartScreenSceneId = menu.Id,
            InputBindings = [new("F2", ScreenButtonAction.MainMenu, null)] };
        var path = Path.Combine(source, "slots.novell"); await new JsonProjectStore().SaveAsync(path, project);
        var plan = Path.Combine(directory, "plan.json");
        File.WriteAllText(plan, """
            [{"Kind":"tick"},{"Kind":"quicksave"},{"Kind":"quickload"},
             {"Kind":"tick","Seconds":2},{"Kind":"quicksave"},{"Kind":"next"},
             {"Kind":"key","Key":"F2"},{"Kind":"slots"},{"Kind":"continue"},
             {"Kind":"key","Key":"F2"},{"Kind":"quickload"},{"Kind":"slots"}]
            """);
        using var seed = new GameSession(project, story.Id, "ru"); seed.NewGame(0);
        var legacy = GameSave.Serialize(seed.Player.Execution);
        seed.Player.Next(); seed.Player.Tick(TimeSpan.FromSeconds(9));
        var latest = GameSlotStore.Prepare(GameSlotStore.Capture(seed, "Последний совместимый", DateTimeOffset.UtcNow.AddDays(1)));
        foreach (var packaged in new[] { false, true })
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = packaged ? Path.Combine(local, "NovellMaker", "Games", project.Id.ToString())
                : Path.Combine(local, "NovellMaker", "Saves", project.Id.ToString());
            var quick = packaged ? Path.Combine(profile, "quicksave.json") : Path.Combine(local, "NovellMaker", "Saves", project.Id + ".json");
            var store = new GameSlotStore(project, Path.Combine(profile, "slots"), quick);
            await store.SaveAsync("latest", latest); File.WriteAllText(quick, legacy);
            var output = Path.Combine(directory, "export-" + Guid.NewGuid().ToString("N"));
            var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
            if (packaged)
            {
                await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService())
                    .ExportAsync(project, source, story.Id, output);
                runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
            }
            var label = packaged ? "export" : "preview"; var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var value in new[] { "--project", path, "--assets-root", source, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(value);
            else
            { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            foreach (var value in new[] { "--smoke-output", Path.Combine(directory, label + ".png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Slot smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            using var json = JsonDocument.Parse(File.ReadAllText(report)); var result = json.RootElement; var trace = result.GetProperty("ActionTrace");
            Assert.AreEqual("Menu", trace[0].GetProperty("SessionPhase").GetString()); Assert.IsFalse(trace[1].GetProperty("Success").GetBoolean());
            foreach (var i in Enumerable.Range(2, 10)) Assert.IsTrue(trace[i].GetProperty("Success").GetBoolean(), $"Step {i} failed.");
            Assert.AreEqual(story.Lines[0].Id, trace[2].GetProperty("State").GetProperty("LineId").GetGuid());
            Assert.AreEqual(0d, trace[2].GetProperty("State").GetProperty("Time").GetDouble());
            Assert.AreEqual(2, trace[7].GetProperty("Slots").GetArrayLength());
            Assert.AreEqual(story.Lines[1].Id, trace[8].GetProperty("State").GetProperty("LineId").GetGuid());
            Assert.AreEqual(9d, trace[8].GetProperty("State").GetProperty("Time").GetDouble());
            Assert.AreEqual(story.Lines[0].Id, trace[10].GetProperty("State").GetProperty("LineId").GetGuid());
            Assert.AreEqual(2d, trace[10].GetProperty("State").GetProperty("Time").GetDouble());
            Assert.IsGreaterThan(0, result.GetProperty("PendingSlotDrawFrames").GetInt32());
            Assert.AreEqual(0, result.GetProperty("Diagnostics").GetArrayLength());
            using var saved = JsonDocument.Parse(File.ReadAllText(quick)); Assert.AreEqual(2, saved.RootElement.GetProperty("Version").GetInt32());
            Assert.AreEqual(GameSlotStatus.Compatible, (await store.ReadAsync("quicksave")).Info.Status);
            Assert.IsEmpty(Directory.GetFiles(profile, "*.tmp", SearchOption.AllDirectories));
            // Remove only the two files created for this unique test project.
            await store.DeleteAsync("quicksave"); await store.DeleteAsync("latest");
        }
    }
}
