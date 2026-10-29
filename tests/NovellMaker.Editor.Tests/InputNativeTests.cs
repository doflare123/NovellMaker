using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens;
using System.Collections.Immutable;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class InputNativeTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExtendedInputRoutesAndRendersInPreviewAndExport(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage93-input", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Расширенный ввод")] };
        var project = ExampleInputModule.AddTo(NovelProject.Create("Ввод") with { Scenes = [story] });
        var modal = ScreenAsset.Create("Модальный слой").ToScene();
        project = project with { Scenes = project.Scenes.Add(modal), InputBindings = project.InputBindings.Add(new("F10", ScreenButtonAction.Toggle, modal.Id)) };
        var file = Path.Combine(directory, "input.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        var plan = Path.Combine(directory, "input.plan.json"); File.WriteAllText(plan, """
            [{"Kind":"key","Key":"F12"},
             {"Kind":"pointer_move","X":220,"Y":400},{"Kind":"pointer_down","X":220,"Y":400},
             {"Kind":"pointer_move","X":620,"Y":420},{"Kind":"pointer_up","X":620,"Y":420},
             {"Kind":"key_down","Key":"Right"},{"Kind":"key_up","Key":"Right"},
             {"Kind":"key","Key":"F12"},{"Kind":"key","Key":"F12"},
             {"Kind":"pointer_down","X":220,"Y":400},{"Kind":"focus_lost"},
             {"Kind":"pointer_up","X":220,"Y":400},{"Kind":"tab"},
             {"Kind":"key_down","Key":"Right"},{"Kind":"key_up","Key":"Right"},
             {"Kind":"click","X":850,"Y":490},
             {"Kind":"pointer_down","X":220,"Y":400},{"Kind":"pointer_move","X":1500,"Y":800},
             {"Kind":"pointer_up","X":1500,"Y":800},
             {"Kind":"pointer_down","X":1080,"Y":676},{"Kind":"key","Key":"F10"},
             {"Kind":"key_down","Key":"Left"},{"Kind":"key_up","Key":"Left"},
             {"Kind":"pointer_move","X":300,"Y":300},{"Kind":"pointer_up","X":300,"Y":300},
             {"Kind":"key","Key":"F10"},{"Kind":"key_down","Key":"Left"},{"Kind":"key_up","Key":"Left"}]
            """);
        var report = Path.Combine(directory, "input.json");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var arg in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
        foreach (var arg in new[] { "--smoke-output", Path.Combine(directory, "input.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Input native smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var trace = json.RootElement.GetProperty("ActionTrace");
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
        Assert.IsTrue(trace.EnumerateArray().All(t => t.GetProperty("Success").GetBoolean()));
        JsonElement Marker(int step) => trace[step].GetProperty("ScreenNodes").EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "drag");
        double X(int step) => Marker(step).GetProperty("Bounds").GetProperty("X").GetDouble();
        Assert.AreEqual(600, X(3)); Assert.AreEqual(620, X(5)); Assert.AreEqual(200, X(8));
        Assert.AreEqual(220, X(13)); Assert.AreEqual(200, X(15)); Assert.AreEqual(1060, X(17));
        Assert.IsTrue(trace[1].GetProperty("ScreenTexts").EnumerateArray().Any(n => n.GetString() == "Наведение"));
        Assert.IsTrue(trace[18].GetProperty("ScreenTexts").EnumerateArray().Any(n => n.GetString() == "Захват снят"));
        Assert.AreEqual(1060, X(26)); Assert.AreEqual(1060, X(27));
    }
}
