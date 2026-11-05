using System.Diagnostics;
using System.Collections.Immutable;
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
            DialogueLine.Create("ru", "Вторая прочитанная реплика."), DialogueLine.Create("ru", "Новая непрочитанная реплика.\n" +
                string.Concat(Enumerable.Repeat("Long multilingual history: Привет, мир. 日本語 😀\n", 40)))] };
        var project = ReadingControlsModule.AddTo(AudioFixture.Attach(NovelProject.Create("Проверка чтения") with { Scenes = [story], InputBindings = [
            new("H", ScreenButtonAction.History, null), new("A", ScreenButtonAction.AutoRead, null),
            new("S", ScreenButtonAction.SkipRead, null), new("R", ScreenButtonAction.ReadingSettings, null)] }, folder), story.Id);
        // Author edits are saved/exported as ordinary scene data, including localized labels and geometry.
        var labels = new Dictionary<string, string> { ["close"] = "Close", ["error"] = "Action failed. Try again.",
            ["empty"] = "No dialogue history yet.", ["previous"] = "Earlier", ["next"] = "Later", ["page_previous"] = "Previous page",
            ["page_next"] = "Next page", ["replay"] = "Replay voice", ["speed_label"] = "Text speed (characters/s)",
            ["instant_speed"] = "Instant display", ["gradual"] = "Gradual",
            ["slower"] = "Slower", ["faster"] = "Faster", ["instant"] = "Instant", ["delay_label"] = "Auto delay (seconds)",
            ["delay_down"] = "-0.5 s", ["delay_up"] = "+0.5 s", ["wait_voice"] = "Toggle voice wait",
            ["wait_on"] = "Wait for voice: yes", ["wait_off"] = "Wait for voice: no", ["hint"] = "Skip stops at unread dialogue and choices." };
        project = project with { Scenes = [.. project.Scenes.Select(s => s.Id == story.Id ? s : s with { Canvas = s.Canvas with
        { Nodes = [.. s.Canvas.Nodes.Select(n => n.Name == "title" ? n with { Text = s.Canvas.References.ContainsKey("body") ? "Backlog" : "Reading preferences", Bounds = new(95, 40, 1000, 36) }
            : labels.TryGetValue(n.Name, out var label) ? n with { Text = label } : n)] } })] };
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
             {"Kind":"tick","Seconds":0.1},{"Kind":"key","Key":"H"},
             {"Kind":"click","X":700,"Y":610},{"Kind":"click","X":500,"Y":610},
             {"Kind":"back"},{"Kind":"key","Key":"R"},
             {"Kind":"click","X":100,"Y":200},{"Kind":"click","X":100,"Y":200},{"Kind":"click","X":100,"Y":200},
             {"Kind":"click","X":600,"Y":200},{"Kind":"click","X":900,"Y":200},{"Kind":"click","X":400,"Y":200}]
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
        Assert.IsTrue(trace[4].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Backlog"));
        Assert.IsTrue(trace[7].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Reading preferences"));
        Assert.IsTrue(trace[8].GetProperty("ScreenNodes").EnumerateArray().Any(n => n.GetProperty("Name").GetString() == "wait_off" && n.GetProperty("Visible").GetBoolean()));
        Assert.IsTrue(trace[4].GetProperty("ScreenNodes").EnumerateArray().Any(n => n.GetProperty("Text").GetString() == "Backlog" && n.GetProperty("Bounds").GetProperty("X").GetDouble() == 95));
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
        string Body(int step) => trace[step].GetProperty("ScreenNodes").EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "body").GetProperty("Text").GetString()!;
        Assert.AreNotEqual(Body(17), Body(18)); Assert.AreEqual(Body(17), Body(19));
        Assert.IsTrue(Body(17).Contains("日本語", StringComparison.Ordinal));
        JsonElement Node(int step, string name) => trace[step].GetProperty("ScreenNodes").EnumerateArray().Single(n => n.GetProperty("Name").GetString() == name);
        Assert.AreEqual("10", Node(24, "speed").GetProperty("Text").GetString());
        Assert.IsFalse(Node(24, "slower").GetProperty("Enabled").GetBoolean());
        Assert.IsFalse(Node(25, "speed").GetProperty("Visible").GetBoolean());
        Assert.IsTrue(Node(25, "instant_speed").GetProperty("Visible").GetBoolean());
        Assert.AreEqual("Instant display", Node(25, "instant_speed").GetProperty("Text").GetString());
        Assert.IsFalse(Node(25, "slower").GetProperty("Enabled").GetBoolean());
        Assert.IsFalse(Node(25, "faster").GetProperty("Enabled").GetBoolean());
        Assert.AreEqual("10", Node(26, "speed").GetProperty("Text").GetString());
        Assert.IsTrue(Node(26, "speed").GetProperty("Visible").GetBoolean());
        Assert.AreEqual("20", Node(27, "speed").GetProperty("Text").GetString());
    }
}
