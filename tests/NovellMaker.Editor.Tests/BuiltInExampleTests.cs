using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class BuiltInExampleTests
{
    [TestMethod]
    public void DeletionPrunesDirectActionTargetsWithoutResurrectingLegacyLines()
    {
        var line = NovellMaker.Core.Scenes.DialogueLine.Create("ru", "Текст");
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("Сцена"); var text = Guid.NewGuid();
        scene = scene with { Lines = [line], Actions = [new JumpAction(Guid.NewGuid(), scene.Id, text), new TextAction(text, line.Id)] };
        var project = NovelProject.Create("Удаление") with { Scenes = [scene with { Lines = [] }] };
        var pruned = ActionReferences.Prune(project);
        Assert.IsTrue(pruned.Scenes[0].UseActions); Assert.IsTrue(pruned.Scenes[0].Actions.IsEmpty);
        ScenarioProgram.Validate(pruned);
        Assert.IsTrue(new ScenarioPlayer(pruned, scene.Id, "ru").Settled().State.Completed);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DefaultExampleRunsBothBranchesAndRestoresInFlightAndMinigameSaves(bool win)
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.Example.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var project = BuiltInExample.Create(directory);
            var path = Path.Combine(directory, "example.novell");
            await new JsonProjectStore().SaveAsync(path, project);
            project = await new JsonProjectStore().LoadAsync(path);
            var scene = project.Scenes[0]; var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
            Assert.IsNull(player.State.Error);
            player.TickSettled(TimeSpan.FromSeconds(2));
            var saved = GameSave.Serialize(player); var first = player.State;
            player.TickSettled(TimeSpan.FromSeconds(2)); GameSave.Load(player, saved);
            Assert.AreEqual(first.Placements.Values.First(p => p.X > 0.4).X, player.State.Placements.Values.First(p => p.X > 0.4).X);
            Assert.IsTrue(player.AdvanceSettled()); Assert.IsFalse(player.AdvanceSettled());
            Assert.IsTrue(player.ClickSettled(player.State.Hotspots[0].ObjectId));
            var game = GameSave.Serialize(player);
            player.TickSettled(TimeSpan.FromSeconds(1)); GameSave.Load(player, game);
            var button = player.State.Hotspots.Single(h => h.Label == (win ? "4" : "5"));
            Assert.IsTrue(player.ClickSettled(button.ObjectId));
            Assert.AreEqual(win ? "win" : "lose", player.State.Variables["minigame.result"]);
            StringAssert.Contains(player.Frame.Text, win ? "Ветка успеха" : "Ветка неудачи");
            Assert.IsTrue(player.AdvanceSettled()); Assert.IsNotNull(player.State.Choice);
            Assert.IsTrue(player.ChooseSettled(player.State.Choice.Options[1].Id));
            Assert.IsTrue(player.AdvanceSettled()); Assert.IsTrue(player.State.Completed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task NativeExampleRendersMovementAndRestoresSaveBeforeClickLuaAndBranch()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1") Assert.Inconclusive("Set NOVELLMAKER_NATIVE_SMOKE=1.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NovellMaker.slnx"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts", "screenshots", "actions-example");
        Directory.CreateDirectory(directory);
        var project = BuiltInExample.Create(directory);
        var projectPath = Path.Combine(directory, "example.novell"); await new JsonProjectStore().SaveAsync(projectPath, project);
        var planPath = Path.Combine(directory, "plan.json");
        await File.WriteAllTextAsync(planPath, """
            [{"Kind":"tick","Seconds":2},{"Kind":"save"},{"Kind":"tick","Seconds":3},{"Kind":"load"},
             {"Kind":"next"},{"Kind":"click","X":650,"Y":300},{"Kind":"save"},
             {"Kind":"tick","Seconds":1},{"Kind":"load"},{"Kind":"click","X":500,"Y":300}]
            """);
        var report = Path.Combine(directory, "report.json"); var screenshot = Path.Combine(directory, "runtime-actions.png");
        var runtime = Path.Combine(AppContext.BaseDirectory, "Runtime", OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime");
        if (!File.Exists(runtime)) runtime = Path.Combine(root.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", Path.GetFileName(runtime));
        var start = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        foreach (var argument in new[] { "--project", projectPath, "--assets-root", directory, "--scene", project.Scenes[0].Id.ToString(), "--language", "ru",
            "--smoke-output", screenshot, "--smoke-report", report, "--smoke-plan", planPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Native action smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await errors);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var trace = json.RootElement.GetProperty("ActionTrace"); Assert.AreEqual(10, trace.GetArrayLength());
        Assert.AreEqual(trace[1].GetProperty("State").GetRawText(), trace[3].GetProperty("State").GetRawText());
        Assert.AreEqual(trace[6].GetProperty("State").GetRawText(), trace[8].GetProperty("State").GetRawText());
        Assert.IsTrue(trace.EnumerateArray().All(s => s.GetProperty("Success").GetBoolean()));
        StringAssert.Contains(trace[9].GetProperty("Text").GetString()!, "Ветка успеха");
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.IsGreaterThan(0, json.RootElement.GetProperty("PendingLuaDrawFrames").GetInt32(), "MonoGame must render frames while Lua is pending.");
        using var bitmap = SKBitmap.Decode(screenshot); Assert.IsNotNull(bitmap);
        // At restored t=2 the traveller is at x≈0.5675; later t=5 was undone.
        var actor = project.Characters[1].Id;
        var x = trace[3].GetProperty("State").GetProperty("Placements").GetProperty(actor.ToString()).GetProperty("X").GetDouble();
        Assert.AreEqual(0.5675, x, 1e-8);
        Assert.AreEqual(new SKColor(183, 146, 100), bitmap.GetPixel(720, 380), "The real MonoGame frame must contain the restored moving sprite.");
    }
}
