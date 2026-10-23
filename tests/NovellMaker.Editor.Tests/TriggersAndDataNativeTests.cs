using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class TriggersAndDataNativeTests
{
    [TestMethod]
    [DataRow(false,1)] [DataRow(true,1)] [DataRow(false,2)] [DataRow(true,2)]
    public async Task AuthorTriggersAndDataMigrateProfilesSurviveOldSlotAndRestart(bool packaged, int legacyVersion)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "stage6-triggers", (packaged ? "export" : "preview") + "-v" + legacyVersion);
        var source = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(source);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Первая фраза"), DialogueLine.Create("ru", "Вторая фраза")] };
        var project = ExampleTriggersModule.AddTo(ExamplePlayerSettingsModule.AddTo(NovelProject.Create("Профиль игрока") with { Scenes = [story],
            Audio = new() { Mixer = System.Collections.Immutable.ImmutableDictionary<AudioGroup, AudioBus>.Empty.Add(AudioGroup.Master, new(.6f)).Add(AudioGroup.Voice, new(.4f)) } }));
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
        // A real v1 file, written before this runtime knew achievements. Reading alone must not rewrite it.
        await store.SaveAsync(PlayerSettings.Defaults(project.Audio));
        var legacy = JsonNode.Parse(File.ReadAllText(profilePath))!;
        legacy["Version"] = legacyVersion; legacy.AsObject().Remove("Data");
        if (legacyVersion == 2) legacy["Achievements"] = new JsonArray("legacy_mark");
        File.WriteAllText(profilePath, legacy.ToJsonString());
        var legacyBytes = File.ReadAllBytes(profilePath);
        using var readOnlyRun = await Run("migration-read", """
            [{"Kind":"tick"}]
            """);
        CollectionAssert.AreEqual(legacyBytes, File.ReadAllBytes(profilePath));
        using var first = await Run("first", """
            [{"Kind":"tick"},{"Kind":"save"},{"Kind":"key","Key":"F8"},
             {"Kind":"click","X":350,"Y":490},{"Kind":"click","X":350,"Y":490},
             {"Kind":"back"},{"Kind":"next"},{"Kind":"load"},{"Kind":"key","Key":"F8"},
             {"Kind":"back"},{"Kind":"key","Key":"F9"},{"Kind":"click","X":680,"Y":201},
             {"Kind":"back"},{"Kind":"key","Key":"F8"}]
            """);
        var trace = first.RootElement.GetProperty("ActionTrace");
        Assert.AreEqual(story.Lines[0].Id, trace[7].GetProperty("State").GetProperty("LineId").GetGuid());
        foreach (var index in new[] {3, 4, 8, 13})
        {
            var labels = trace[index].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray();
            CollectionAssert.Contains(labels, "Отмечено: Первый шаг");
        }
        var saved = await store.ReadAsync(PlayerSettings.Defaults(project.Audio));
        CollectionAssert.AreEquivalent((legacyVersion == 2 ? new[] { "first_steps", "legacy_mark" } : new[] { "first_steps" }), saved.Data.Keys.ToArray());
        Assert.AreEqual(.5f, saved.Settings.Master.Volume, .00001);
        Assert.AreEqual(3, JsonNode.Parse(File.ReadAllText(profilePath))!["Version"]!.GetValue<int>());
        using var restarted = await Run("restart", """
            [{"Kind":"key","Key":"F8"},{"Kind":"click","X":350,"Y":490},
             {"Kind":"back"},{"Kind":"key","Key":"F9"}]
            """);
        var restartTrace = restarted.RootElement.GetProperty("ActionTrace");
        CollectionAssert.Contains(restartTrace[0].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray(), "Отмечено: Первый шаг");
        CollectionAssert.Contains(restartTrace[3].GetProperty("ScreenTexts").EnumerateArray().Select(e => e.GetString()).ToArray(), "Общая громкость: 50%");
        CollectionAssert.AreEquivalent((legacyVersion == 2 ? new[] { "first_steps", "legacy_mark" } : new[] { "first_steps" }), (await store.ReadAsync(PlayerSettings.Defaults(project.Audio))).Data.Keys.ToArray());

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
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Achievements native smoke timed out."); }
            Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
            var json = JsonDocument.Parse(File.ReadAllText(report));
            var diagnostics = json.RootElement.GetProperty("Diagnostics");
            if (label == "migration-read")
                CollectionAssert.AreEqual(new[] { $"Профиль v{legacyVersion} прочитан; v3 будет записан при следующем изменении." }, diagnostics.EnumerateArray().Select(e => e.GetString()).ToArray());
            else Assert.AreEqual(0, diagnostics.GetArrayLength(), diagnostics.ToString());
            Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
            Assert.IsTrue(json.RootElement.GetProperty("ActionTrace").EnumerateArray().All(e => e.GetProperty("Success").GetBoolean()));
            return json;
        }
    }
}
