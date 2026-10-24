using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RandomNativeTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task RandomCheckpointReplayIsIdenticalInPreviewAndStandaloneAcrossReopenAndRestart(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage7-rng", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Первая фраза"), DialogueLine.Create("ru", "Вторая фраза")] };
        var project = ExampleLuaRandomModule.AddTo(NovelProject.Create("RNG") with { Scenes = [story] });
        var file = Path.Combine(directory, "rng.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
            Assert.IsFalse(Directory.EnumerateFiles(output, "*.lua", SearchOption.AllDirectories).Any());
        }
        var license = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(runtime)!, "Licenses", "PCG.txt"));
        StringAssert.Contains(license, "Copyright 2014 Melissa O'Neill"); StringAssert.Contains(license, "Apache License");
        using var first = await Run("first", """
            [{"Kind":"save"},{"Kind":"key","Key":"F2"},
             {"Kind":"click","X":350,"Y":490},{"Kind":"click","X":350,"Y":490},
             {"Kind":"click","X":850,"Y":490},{"Kind":"click","X":350,"Y":490},
             {"Kind":"back"},{"Kind":"next"},{"Kind":"load"},{"Kind":"key","Key":"F2"},
             {"Kind":"click","X":350,"Y":490}]
            """);
        var trace = first.RootElement.GetProperty("ActionTrace");
        const string initial = "Нажмите «Следующее число»";
        Check(trace, 1, initial); Check(trace, 2, "Число: 1783"); Check(trace, 3, "Число: 3097");
        Check(trace, 4, "Число: 3097"); Check(trace, 5, "Число: 5824"); Check(trace, 9, initial); Check(trace, 10, "Число: 1783");
        Assert.AreEqual(story.Lines[0].Id, trace[8].GetProperty("State").GetProperty("LineId").GetGuid());
        using var restart = await Run("restart", """
            [{"Kind":"key","Key":"F2"},{"Kind":"click","X":350,"Y":490},
             {"Kind":"click","X":850,"Y":490},{"Kind":"click","X":350,"Y":490}]
            """);
        var restarted = restart.RootElement.GetProperty("ActionTrace");
        Check(restarted, 1, "Число: 1783"); Check(restarted, 2, "Число: 1783"); Check(restarted, 3, "Число: 3097");
        static void Check(JsonElement trace, int index, string text) => CollectionAssert.Contains(trace[index].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray(), text);

        async Task<JsonDocument> Run(string label, string planText)
        {
            var plan = Path.Combine(directory, label + ".plan.json"); await File.WriteAllTextAsync(plan, planText);
            var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var argument in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(argument);
            else
            {
                start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent");
                start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
            }
            foreach (var argument in new[] { "--smoke-output", Path.Combine(directory, label + ".png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("RNG native smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
            Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
            Assert.IsTrue(json.RootElement.GetProperty("ActionTrace").EnumerateArray().All(e => e.GetProperty("Success").GetBoolean()));
            return json;
        }
    }
}
