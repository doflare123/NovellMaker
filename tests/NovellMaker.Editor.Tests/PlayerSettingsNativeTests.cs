using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlayerSettingsNativeTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task AuthorSettingsSurviveOldSlotAndRestartWithRealWindowMixerAndWorker(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage6-settings", packaged ? "export" : "preview");
        var source = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(source);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Первая фраза"), DialogueLine.Create("ru", "Вторая фраза")] };
        var project = ExamplePlayerSettingsModule.AddTo(NovelProject.Create("Профиль игрока") with { Scenes = [story],
            Audio = new() { Mixer = System.Collections.Immutable.ImmutableDictionary<AudioGroup, AudioBus>.Empty.Add(AudioGroup.Master, new(.6f)).Add(AudioGroup.Voice, new(.4f)) } });
        var file = Path.Combine(source, "settings.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(source, "game");
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService()).ExportAsync(project, source, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profilePath = Path.Combine(local, "NovellMaker", packaged ? "Games" : "Saves", project.Id.ToString(), "profile.json");
        var store = new PlayerProfileStore(project.Id, profilePath);
        Assert.AreEqual(PlayerProfileStatus.Missing, (await store.ReadAsync(PlayerSettings.Defaults(project.Audio))).Status);
        var first = await Run("first", """
            [{"Kind":"tick"},{"Kind":"key","Key":"F9"},
             {"Kind":"click","X":680,"Y":201},{"Kind":"click","X":990,"Y":277},
             {"Kind":"click","X":780,"Y":429},{"Kind":"click","X":500,"Y":520},
             {"Kind":"back"},{"Kind":"save"},{"Kind":"next"},{"Kind":"load"},
             {"Kind":"key","Key":"F9"}]
            """);
        using (first)
        {
            var trace = first.RootElement.GetProperty("ActionTrace");
            Assert.AreEqual(story.Lines[0].Id, trace[9].GetProperty("State").GetProperty("LineId").GetGuid());
            Assert.AreEqual(.5, trace[9].GetProperty("PlayerSettings").GetProperty("Master").GetProperty("Volume").GetDouble(), .00001);
            Assert.IsTrue(trace[5].GetProperty("Fullscreen").GetBoolean()); Assert.IsTrue(trace[10].GetProperty("Fullscreen").GetBoolean());
            var labels = trace[10].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray();
            CollectionAssert.Contains(labels, "Общая громкость: 50%"); CollectionAssert.Contains(labels, "Режим окна: полноэкранный");
            var mixer = first.RootElement.GetProperty("AudioMixer"); Assert.IsTrue(mixer.GetProperty("Sound").GetProperty("Muted").GetBoolean());
            Assert.AreEqual(.5, mixer.GetProperty("Voice").GetProperty("Volume").GetDouble(), .00001);
        }
        var saved = await store.ReadAsync(PlayerSettings.Defaults(project.Audio));
        Assert.AreEqual(PlayerProfileStatus.Loaded, saved.Status); Assert.IsTrue(saved.Settings.Fullscreen);
        Assert.AreEqual(.5f, saved.Settings.Master.Volume, .00001); Assert.IsTrue(saved.Settings.Sound.Muted);
        using var restarted = await Run("restart", """
            [{"Kind":"key","Key":"F9"},{"Kind":"click","X":500,"Y":520}]
            """);
        var restartTrace = restarted.RootElement.GetProperty("ActionTrace");
        Assert.IsTrue(restartTrace[0].GetProperty("Fullscreen").GetBoolean()); Assert.IsFalse(restartTrace[1].GetProperty("Fullscreen").GetBoolean());
        Assert.AreEqual(.5, restarted.RootElement.GetProperty("AudioMixer").GetProperty("Master").GetProperty("Volume").GetDouble(), .00001);
        var latest = (await store.ReadAsync(PlayerSettings.Defaults(project.Audio))).Settings;
        Assert.IsFalse(latest.Fullscreen); Assert.IsTrue(latest.Sound.Muted);

        async Task<JsonDocument> Run(string label, string planText)
        {
            var plan = Path.Combine(directory, label + ".plan.json"); File.WriteAllText(plan, planText);
            var report = Path.Combine(directory, label + ".json");
            var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (!packaged) foreach (var argument in new[] { "--project", file, "--assets-root", source, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(argument);
            else { start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent"); start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            foreach (var argument in new[] { "--smoke-output", Path.Combine(directory, label + ".png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Settings native smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength(), json.RootElement.GetProperty("Diagnostics").ToString());
            Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
            Assert.IsTrue(json.RootElement.GetProperty("ActionTrace").EnumerateArray().All(e => e.GetProperty("Success").GetBoolean()));
            return json;
        }
    }
}
