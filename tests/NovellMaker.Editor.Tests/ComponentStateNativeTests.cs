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
public sealed class ComponentStateNativeTests
{
    [TestMethod]
    [DataRow(false, 0)] [DataRow(true, 0)] [DataRow(false, 1)] [DataRow(true, 1)] [DataRow(false, 2)] [DataRow(true, 2)]
    public async Task EditableTypedStateExampleRunsInPreviewAndStandaloneAndResetsOnReopen(bool packaged, int exampleKind)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage7-state", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Первая фраза"), DialogueLine.Create("ru", "Вторая фраза")] };
        var initial = NovelProject.Create("Состояние Lua") with { Scenes = [story] };
        var project = exampleKind == 2 ? ExampleLuaLibraryModule.AddTo(initial) : exampleKind == 1 ? ExampleLuaModulesModule.AddTo(initial) : ExampleComponentStateModule.AddTo(initial);
        var file = Path.Combine(directory, "state.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
            Assert.IsFalse(Directory.EnumerateFiles(output, "*.lua", SearchOption.AllDirectories).Any());
        }
        using var first = await Run("first", """
            [{"Kind":"tick"},{"Kind":"save"},{"Kind":"key","Key":"F7"},
             {"Kind":"click","X":350,"Y":490},{"Kind":"click","X":350,"Y":490},
             {"Kind":"click","X":850,"Y":490},{"Kind":"click","X":350,"Y":490},
             {"Kind":"back"},{"Kind":"next"},{"Kind":"load"},{"Kind":"key","Key":"F7"},
             {"Kind":"click","X":350,"Y":490},{"Kind":"back"},{"Kind":"key","Key":"F7"}]
            """);
        var trace = first.RootElement.GetProperty("ActionTrace");
        Check(2, "Готово к нажатию"); Check(3, "Шагов: 1"); Check(4, "Шагов: 2");
        Check(5, "Готово к нажатию"); Check(6, "Шагов: 1"); Check(10, "Готово к нажатию");
        Check(11, "Шагов: 1"); Check(13, "Готово к нажатию");
        Assert.AreEqual(story.Lines[0].Id, trace[9].GetProperty("State").GetProperty("LineId").GetGuid());
        string Caption(string text) => exampleKind == 2 && text.StartsWith("Шагов: ", StringComparison.Ordinal)
            ? text + (text.EndsWith('2') ? " · Последние: 1, 2" : " · Последние: 1") : text;
        void Check(int index, string text) => CollectionAssert.Contains(trace[index].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray(), Caption(text));
        using var restarted = await Run("restart", """
            [{"Kind":"key","Key":"F7"},{"Kind":"click","X":350,"Y":490}]
            """);
        CollectionAssert.Contains(restarted.RootElement.GetProperty("ActionTrace")[1].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray(), Caption("Шагов: 1"));
        if (exampleKind == 2)
        {
            using var history = await Run("history", """
                [{"Kind":"key","Key":"F7"},{"Kind":"click","X":350,"Y":490},
                 {"Kind":"click","X":350,"Y":490},{"Kind":"click","X":350,"Y":490},
                 {"Kind":"click","X":350,"Y":490}]
                """);
            CollectionAssert.Contains(history.RootElement.GetProperty("ActionTrace")[4].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray(), "Шагов: 4 · Последние: 2, 3, 4");
        }

        async Task<JsonDocument> Run(string label, string planText)
        {
            var plan = Path.Combine(directory, label + ".plan.json"); File.WriteAllText(plan, planText.Replace("F7", exampleKind == 2 ? "F3" : exampleKind == 1 ? "F4" : "F7", StringComparison.Ordinal));
            var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var argument in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(argument);
            else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            foreach (var argument in new[] { "--smoke-output", Path.Combine(directory, label + ".png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Component state native smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
            Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
            Assert.IsTrue(json.RootElement.GetProperty("ActionTrace").EnumerateArray().All(e => e.GetProperty("Success").GetBoolean()));
            return json;
        }
    }
}
