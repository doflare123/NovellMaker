using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using NovellMaker.Testing;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReadingNativeTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ReadingControlsHistoryVoiceAndOldSlotWorkInPreviewAndExport(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable native/export smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var folder = Path.Combine(repo!.FullName, "artifacts", "screenshots", "reading", (packaged ? "export-" : "preview-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var story = NovelScene.Create("Чтение") with { Lines = [DialogueLine.Create("ru", "Первая реплика с постепенным появлением текста."),
            DialogueLine.Create("ru", "Вторая прочитанная реплика."), DialogueLine.Create("ru", "Новая непрочитанная реплика.")] };
        var project = ReadingControlsModule.AddTo(AudioFixture.Attach(NovelProject.Create("Проверка чтения") with { Scenes = [story], InputBindings = [
            new("H", ScreenButtonAction.History, null), new("A", ScreenButtonAction.AutoRead, null),
            new("S", ScreenButtonAction.SkipRead, null), new("R", ScreenButtonAction.ReadingSettings, null)] }, folder), story.Id);
        var file = Path.Combine(folder, "reading.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", config, "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(folder, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, folder, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        var plan = Path.Combine(folder, "plan.json");
        File.WriteAllText(plan, """
            [{"Kind":"tick"},{"Kind":"tick","Seconds":0.1},{"Kind":"continue_text"},{"Kind":"save"},
             {"Kind":"key","Key":"H"},{"Kind":"click","X":1000,"Y":610},{"Kind":"back"},
             {"Kind":"key","Key":"R"},{"Kind":"click","X":300,"Y":450},{"Kind":"back"},
             {"Kind":"key","Key":"A"},{"Kind":"tick","Seconds":3},{"Kind":"continue_text"},
             {"Kind":"load"},{"Kind":"key","Key":"S"},{"Kind":"tick","Seconds":0.1},
             {"Kind":"tick","Seconds":0.1},{"Kind":"key","Key":"H"}]
            """);
        var report = Path.Combine(folder, "report.json");
        var start = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = folder };
        if (!packaged) foreach (var argument in new[] { "--project", file, "--assets-root", folder, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(argument);
        else { start.Environment["DOTNET_ROOT"] = Path.Combine(folder, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(folder, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
        foreach (var argument in new[] { "--smoke-output", Path.Combine(folder, "history.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Reading smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var trace = json.RootElement.GetProperty("ActionTrace");
        Assert.IsTrue(trace.EnumerateArray().All(s => s.GetProperty("Success").GetBoolean()));
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.IsFalse(trace[1].GetProperty("FullyRevealed").GetBoolean()); Assert.IsTrue(trace[2].GetProperty("FullyRevealed").GetBoolean());
        Assert.IsTrue(trace[1].GetProperty("PresentedScreenTexts").EnumerateArray().Any(t => t.GetString() == "Перв"));
        Assert.IsTrue(trace[2].GetProperty("PresentedScreenTexts").EnumerateArray().Any(t => t.GetString() == story.Lines[0].Texts["ru"]));
        Assert.AreEqual(story.Lines[0].Id, trace[2].GetProperty("State").GetProperty("LineId").GetGuid());
        Assert.AreEqual(trace[4].GetProperty("AudioOpenedSources").GetInt32() + 1, trace[5].GetProperty("AudioOpenedSources").GetInt32());
        Assert.AreEqual(trace[4].GetProperty("State").GetProperty("Time").GetDouble(), trace[5].GetProperty("State").GetProperty("Time").GetDouble());
        Assert.AreEqual("Auto", trace[10].GetProperty("ReadingMode").GetString());
        Assert.AreEqual(story.Lines[1].Id, trace[11].GetProperty("State").GetProperty("LineId").GetGuid());
        Assert.AreEqual("Manual", trace[13].GetProperty("ReadingMode").GetString()); Assert.IsTrue(trace[13].GetProperty("FullyRevealed").GetBoolean());
        Assert.AreEqual(1, trace[13].GetProperty("State").GetProperty("History").GetArrayLength()); Assert.AreEqual(2, trace[13].GetProperty("ReadLineCount").GetInt32());
        Assert.AreEqual("SkipRead", trace[14].GetProperty("ReadingMode").GetString());
        Assert.AreEqual(story.Lines[2].Id, trace[16].GetProperty("State").GetProperty("LineId").GetGuid());
        Assert.AreEqual("Manual", trace[16].GetProperty("ReadingMode").GetString()); Assert.IsFalse(trace[16].GetProperty("FullyRevealed").GetBoolean());
        Assert.AreEqual(3, trace[17].GetProperty("State").GetProperty("History").GetArrayLength());
    }
}
