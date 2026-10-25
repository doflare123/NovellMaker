using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LuaDiagnosticsNativeTests
{
    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task LogsAndFaultLocationsWorkInPreviewAndStandaloneAndFailedChangesAreRejected(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage7-diagnostics", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("ru", "Первая фраза")] };
        var project = ExampleLuaDiagnosticsModule.AddTo(NovelProject.Create("Diagnostics") with { Scenes = [story] });
        var handler = project.Scripts.Last(); var file = Path.Combine(directory, "diagnostics.novell");
        await new JsonProjectStore().SaveAsync(file, project);
        LuaAuthoringTools.Install(directory); // Tooling files must never become exported executable resources.
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
            Assert.IsFalse(Directory.EnumerateFiles(output, "*.lua", SearchOption.AllDirectories).Any());
        }
        var plan = Path.Combine(directory, "plan.json");
        await File.WriteAllTextAsync(plan, """
            [{"Kind":"key","Key":"F1"},{"Kind":"click","X":350,"Y":490},
             {"Kind":"click","X":850,"Y":490},{"Kind":"back"},{"Kind":"key","Key":"F1"},
             {"Kind":"click","X":350,"Y":490}]
            """);
        var report = Path.Combine(directory, "report.json");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var arg in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        else
        {
            start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent");
            start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        }
        foreach (var arg in new[] { "--smoke-output", Path.Combine(directory, "result.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Lua diagnostics native smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var trace = json.RootElement.GetProperty("ActionTrace");
        Assert.IsTrue(trace.EnumerateArray().All(e => e.GetProperty("Success").GetBoolean()));
        Assert.IsTrue(trace[2].GetProperty("ScreenTexts").EnumerateArray().Any(e => e.GetString() == "Сообщение записано из click"));
        Assert.IsFalse(trace[2].GetProperty("ScreenTexts").EnumerateArray().Any(e => e.GetString() == "Этот текст не должен примениться"));
        var journal = json.RootElement.GetProperty("LuaJournal"); Assert.AreEqual(6, journal.GetArrayLength());
        var fault = journal[3]; Assert.AreEqual(handler.Id, fault.GetProperty("Data").GetProperty("ScriptId").GetGuid());
        Assert.AreEqual("error", fault.GetProperty("Data").GetProperty("Level").GetString());
        var errorLine = ExampleLuaDiagnosticsModule.Source.Split('\n').Select((line, index) => (line, index)).Single(item => item.line.Trim() == "missing()").index + 1;
        Assert.AreEqual(errorLine, fault.GetProperty("Data").GetProperty("Line").GetInt32());
        Assert.AreEqual("click", fault.GetProperty("Event").GetString());
        var journalPath = packaged ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Games", project.Id.ToString(), "lua-diagnostics.log") : LuaAuthoringTools.JournalPath(project.Id);
        var persisted = await File.ReadAllTextAsync(journalPath); StringAssert.Contains(persisted, $"event=click line={errorLine}");
        StringAssert.Contains(persisted, handler.Id.ToString("N")); Assert.AreEqual(7, File.ReadAllLines(journalPath).Length);
        await File.WriteAllTextAsync(Path.Combine(directory, "journal-path.txt"), journalPath);
    }
}
