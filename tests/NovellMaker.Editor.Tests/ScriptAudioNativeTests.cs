using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using NovellMaker.Testing;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ScriptAudioNativeTests
{
    [TestMethod]
    [DataRow(false, 20d)] [DataRow(true, 20d)] [DataRow(false, 5d)] [DataRow(true, 5d)]
    public async Task RealtimeMiniGameUpdatesDoNotInterruptTheNativeAudioSource(bool packaged, double updatesPerSecond)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage9-audio-realtime", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Непрерывный звук")] };
        var project = AudioFixture.Attach(NovelProject.Create("Звук и update") with { Scenes = [story] }, directory);
        project = ExampleAudioModule.AddTo(project with { Audio = project.Audio with { SceneSounds = ImmutableDictionary<Guid, Guid>.Empty,
            LineEffects = ImmutableDictionary<Guid, Guid>.Empty, Voices = ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>>.Empty } });
        var game = project.Scenes.Last(); story = story with { ScriptBlocks = [new(null, ScriptPhase.SceneStart,
            new StartMinigameAction(Guid.NewGuid(), game.Id, LuaValue.FromObject([])))] };
        project = project with { Scenes = project.Scenes.SetItem(0, story).SetItem(1, game with { Canvas = game.Canvas with
            { Minigame = new MinigameDefinition { UpdatesPerSecond = updatesPerSecond } } }),
            Scripts = project.Scripts.SetItem(project.Scripts.Length - 1, project.Scripts.Last() with { Source = """
                local updates=0
                return {on_load=function() ui.play_audio(audio.sample,'sound',true,0.25) end,
                  on_event=function(e)
                    if e.kind=='update' then updates=updates+1;ui.set_state({updates=updates}) end
                  end}
                """ }) };
        var file = Path.Combine(directory, "realtime.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        var reportPath = Path.Combine(directory, "realtime.json");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var arg in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
        foreach (var arg in new[] { "--smoke-output", Path.Combine(directory, "realtime.png"), "--smoke-report", reportPath, "--smoke-realtime-seconds", "5" }) start.ArgumentList.Add(arg);
        var wall = Stopwatch.StartNew();
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Realtime audio smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(reportPath)); var report = json.RootElement;
        Assert.AreEqual(0, report.GetProperty("Diagnostics").GetArrayLength()); Assert.AreEqual(0, report.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
        Assert.IsTrue(wall.Elapsed.TotalSeconds >= 4.5); Assert.IsTrue(report.GetProperty("RealtimeGameSeconds").GetDouble() >= 5);
        Assert.IsTrue(report.GetProperty("MinigameState").GetProperty("updates").GetDouble() >= updatesPerSecond * 2);
        Assert.AreEqual(1, report.GetProperty("AudioSources").GetInt32()); Assert.AreEqual(1, report.GetProperty("AudioOpenedSources").GetInt32());
        Assert.IsTrue(report.GetProperty("AudioSubmittedBytes").GetInt64() >= 120000); Assert.IsTrue(report.GetProperty("DrawFrames").GetInt32() >= 100);
        Assert.AreEqual(0, report.GetProperty("AudioPlaybackPauseCount").GetInt32(),
            $"Actual backend pauses: {report.GetProperty("AudioPlaybackPausedSeconds")}s; longest {report.GetProperty("AudioLongestPlaybackPauseSeconds")}s; report {reportPath}");
        Assert.AreEqual(0, report.GetProperty("AudioBufferUnderruns").GetInt32(), "Native streaming buffers must not run dry after playback starts.");
    }

    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task LuaAudioUsesNativeMixerOwnershipAndSaveBarrierInPreviewAndExport(bool packaged, bool minigame)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage9-audio", packaged ? "export" : "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Звук из Lua"), DialogueLine.Create("ru", "Дальше")] };
        var project = AudioFixture.Attach(NovelProject.Create("Звук Lua") with { Scenes = [story] }, directory);
        project = ExampleAudioModule.AddTo(project with { Audio = project.Audio with { SceneSounds = ImmutableDictionary<Guid, Guid>.Empty,
            LineEffects = ImmutableDictionary<Guid, Guid>.Empty, Voices = ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>>.Empty } });
        if (minigame)
        {
            var game = project.Scenes.Last();
            story = story with { ScriptBlocks = [new(null, ScriptPhase.SceneStart, new StartMinigameAction(Guid.NewGuid(), game.Id, LuaValue.FromObject([])))] };
            project = project with { Scenes = project.Scenes.SetItem(0, story).SetItem(1, game with { Canvas = game.Canvas with { Minigame = new MinigameDefinition() } }),
                Scripts = project.Scripts.SetItem(project.Scripts.Length - 1, project.Scripts.Last() with { Source = """
                    local sound=nil
                    return {on_load=function()
                      if not ui.is_restoring() then sound=ui.play_audio(audio.sample,'effect',true,0.25) end
                    end,on_event=function(e)
                      if e.kind=='click' and e.node==refs.reset and sound~=nil then ui.stop_audio(sound)
                      elseif e.kind=='click' and e.node==refs.increment and sound==nil then sound=ui.play_audio(audio.sample,'sound',true,0.5)
                      elseif e.kind=='audio_result' and e.audio_id==sound then sound=nil;ui.set_text(refs.state,e.status) end
                    end}
                    """ }) };
        }
        var file = Path.Combine(directory, "audio.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        var plan = Path.Combine(directory, "audio.plan.json"); File.WriteAllText(plan, minigame
            ? """
              [{"Kind":"tick"},{"Kind":"quicksave"},{"Kind":"click","X":850,"Y":490},
               {"Kind":"quicksave"},{"Kind":"quickload"},{"Kind":"tick"},
               {"Kind":"click","X":350,"Y":490},{"Kind":"quickload"},{"Kind":"back"}]
              """
            : """
              [{"Kind":"key","Key":"F10"},{"Kind":"click","X":350,"Y":490},
               {"Kind":"click","X":350,"Y":490},{"Kind":"click","X":850,"Y":490},
               {"Kind":"back"},{"Kind":"key","Key":"F10"},{"Kind":"click","X":350,"Y":490},
               {"Kind":"back"},{"Kind":"key","Key":"F10"}]
              """);
        var report = Path.Combine(directory, "audio.json");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var arg in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
        foreach (var arg in new[] { "--smoke-output", Path.Combine(directory, "audio.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Audio native smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var trace = json.RootElement.GetProperty("ActionTrace");
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
        int Sources(int i) => trace[i].GetProperty("AudioSources").GetInt32();
        bool Text(int i, string text) => trace[i].GetProperty("ScreenTexts").EnumerateArray().Any(n => n.GetString() == text);
        Assert.IsTrue(trace.EnumerateArray().Where((_, i) => !minigame || i != 1).All(t => t.GetProperty("Success").GetBoolean()));
        if (minigame)
        {
            Assert.AreEqual(1, Sources(0)); Assert.IsFalse(trace[1].GetProperty("Success").GetBoolean());
            Assert.AreEqual(0, Sources(2)); Assert.IsTrue(Text(2, "stopped"));
            Assert.AreEqual(0, Sources(4)); Assert.AreEqual(0, Sources(5)); Assert.AreEqual(1, Sources(6));
            Assert.AreEqual(0, Sources(7)); Assert.AreEqual(0, Sources(8));
        }
        else
        {
            Assert.AreEqual(0, Sources(0)); Assert.AreEqual(1, Sources(1)); Assert.AreEqual(1, Sources(2));
            Assert.AreEqual(.25, trace[1].GetProperty("AudioLastSourceVolume").GetDouble(), .000001);
            Assert.AreEqual(.5, trace[2].GetProperty("AudioLastSourceVolume").GetDouble(), .000001);
            Assert.AreEqual(0, Sources(3)); Assert.IsTrue(Text(3, "stopped")); Assert.AreEqual(1, Sources(6));
            Assert.AreEqual(0, Sources(7)); Assert.AreEqual(0, Sources(8)); Assert.IsTrue(Text(8, "Готово"));
        }
        Assert.IsTrue(trace.EnumerateArray().Any(t => t.GetProperty("AudioSubmittedBytes").GetInt64() > 0));
        Assert.IsTrue(json.RootElement.GetProperty("DrawFrames").GetInt32() > 0);
    }
}
