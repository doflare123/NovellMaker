using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class WindowsExportTests
{
    private string root = null!;
    private string source = null!;
    private string template = null!;
    private NovelProject project = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "NovellMaker.Export.Tests", Guid.NewGuid().ToString("N"));
        source = Path.Combine(root, "source");
        template = Path.Combine(root, "template"); Directory.CreateDirectory(template);
        project = NovellMaker.Testing.AudioFixture.Attach(BuiltInExample.Create(source), source);
        // Small template fixtures exercise packaging and failure handling. A separate test launches the real host.
        foreach (var file in new[] { "NovellMaker.Runtime.exe", "NovellMaker.Runtime.dll", "NovellMaker.Core.dll",
                     "NovellMaker.Runtime.deps.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "SDL2.dll", "openal.dll", "libSkiaSharp.dll" })
            File.WriteAllText(Path.Combine(template, file), "fixture");
        File.WriteAllText(Path.Combine(template, "NovellMaker.Runtime.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[]}}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        var gameRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Games");
        var gameDirectory = Path.GetFullPath(Path.Combine(gameRoot, project.Id.ToString()));
        if (Path.GetDirectoryName(gameDirectory) == gameRoot && Directory.Exists(gameDirectory)) Directory.Delete(gameDirectory, true);
        var full = Path.GetFullPath(root);
        if (full.StartsWith(Path.Combine(Path.GetTempPath(), "NovellMaker.Export.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            Directory.Delete(full, true);
    }

    [TestMethod]
    public async Task ExportCopiesCurrentSnapshotAndOnlyRegisteredAssetsWithoutChangingSource()
    {
        File.WriteAllText(Path.Combine(source, "private.txt"), "Do not export");
        File.WriteAllText(Path.Combine(template, "native.pdb"), "debug symbols");
        var original = Path.Combine(source, "project.novell");
        await new JsonProjectStore().SaveAsync(original, project);
        var originalBytes = await File.ReadAllBytesAsync(original);
        var second = NovellMaker.Core.Scenes.NovelScene.Create("Выбранный старт") with { Lines = [NovellMaker.Core.Scenes.DialogueLine.Create("ru", "Начало игры")] };
        var script = new LuaScriptAsset(Guid.NewGuid(), "export.lua", "game.set('exported_script', 'yes')");
        second = second with { ScriptBlocks = [new(null, NovellMaker.Core.Scenes.ScriptPhase.SceneStart, new RunScriptAction(Guid.NewGuid(), script.Id))] };
        var edited = project with { Name = "Несохранённое название", Scenes = project.Scenes.Add(second), Scripts = project.Scripts.Add(script) };
        var destination = Path.Combine(root, "Игра с пробелами");
        await new WindowsExportService(template, new ImageAssetService()).ExportAsync(edited, source, second.Id, destination);
        var package = GamePackage.Load(destination);
        Assert.AreEqual(edited.Name, package.Title);
        Assert.AreEqual(edited.Id, package.ProjectId);
        Assert.AreEqual(second.Id, package.StartSceneId);
        var exported = await new JsonProjectStore().LoadAsync(Path.Combine(destination, GamePackage.ProjectName));
        Assert.AreEqual("yes", new ScenarioPlayer(exported, second.Id, "ru").State.Variables["exported_script"]);
        Assert.IsTrue(edited.ContentEquals(await new JsonProjectStore().LoadAsync(Path.Combine(destination, GamePackage.ProjectName))));
        foreach (var image in project.Images)
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(source, image.RelativePath)), await File.ReadAllBytesAsync(Path.Combine(destination, image.RelativePath)));
        foreach (var sound in project.Audio.Assets)
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(source, sound.RelativePath)), File.ReadAllBytes(Path.Combine(destination, sound.RelativePath)));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "private.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "native.pdb")));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(original));
        await Assert.ThrowsExactlyAsync<IOException>(() => new WindowsExportService(template, new ImageAssetService()).ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsTrue(edited.ContentEquals(await new JsonProjectStore().LoadAsync(Path.Combine(destination, GamePackage.ProjectName))));
    }

    [TestMethod]
    public async Task MissingOrCorruptImageLeavesNoPartialBuildAndCanBeRetried()
    {
        var image = Path.Combine(source, project.Images[0].RelativePath);
        var bytes = await File.ReadAllBytesAsync(image);
        var exporter = new WindowsExportService(template, new ImageAssetService());
        var destination = Path.Combine(root, "game");
        File.Delete(image);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination)); Assert.HasCount(0, Directory.GetDirectories(root, ".novell-export-*"));
        await File.WriteAllTextAsync(image, "corrupt");
        await Assert.ThrowsAsync<Exception>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination));
        await File.WriteAllBytesAsync(image, bytes);
        await exporter.ExportAsync(project, source, project.Scenes[0].Id, destination);
        Assert.IsTrue(File.Exists(Path.Combine(destination, "NovellMaker.Runtime.exe")));
    }

    [TestMethod]
    public async Task InvalidAudioLeavesNoPartialExport()
    {
        File.WriteAllText(Path.Combine(source, project.Audio.Assets[0].RelativePath), "corrupt");
        var destination = Path.Combine(root, "bad-audio");
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsExportService(template, new ImageAssetService()).ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination)); Assert.HasCount(0, Directory.GetDirectories(root, ".novell-export-*"));
    }

    [TestMethod]
    public async Task FrameworkDependentOrMissingTemplateAndInvalidGraphAreRejected()
    {
        var destination = Path.Combine(root, "game");
        var exporter = new WindowsExportService(template, new ImageAssetService());
        var emptyStart = NovellMaker.Core.Scenes.NovelScene.Create("Пустая стартовая сцена");
        var emptyProject = project with { Scenes = project.Scenes.Add(emptyStart) };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => exporter.ExportAsync(emptyProject, source, emptyStart.Id, destination));
        File.WriteAllText(Path.Combine(template, "NovellMaker.Runtime.runtimeconfig.json"), "{\"runtimeOptions\":{\"framework\":{}}}");
        await Assert.ThrowsExactlyAsync<IOException>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
        InitializeTemplateConfig();
        var broken = project with { Scenes = [project.Scenes[0] with { ScriptBlocks = [new(null, NovellMaker.Core.Scenes.ScriptPhase.SceneStart, new JumpAction(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))] }] };
        await Assert.ThrowsAsync<Exception>(() => exporter.ExportAsync(broken, source, broken.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination));
        File.Delete(Path.Combine(template, "coreclr.dll"));
        await Assert.ThrowsExactlyAsync<IOException>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
    }

    private void InitializeTemplateConfig() => File.WriteAllText(Path.Combine(template, "NovellMaker.Runtime.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[]}}");

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task ExportedGameRunsFromAnotherDirectoryWithNoDotnetAndRestoresQueuedState()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1" || !OperatingSystem.IsWindows())
            Assert.Inconclusive("Set NOVELLMAKER_EXPORT_SMOKE=1 after scripts/prepare-windows-export.ps1 on Windows.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var realTemplate = Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64");
        var destination = Path.Combine(root, "exported game");
        await new WindowsExportService(realTemplate, new ImageAssetService()).ExportAsync(project, source, project.Scenes[0].Id, destination);
        var moved = Path.Combine(root, "перенесённая игра"); Directory.Move(destination, moved);
        // Removing authoring assets proves the exported game cannot fall back to the project directory.
        foreach (var image in project.Images) File.Delete(Path.Combine(source, image.RelativePath));
        foreach (var sound in project.Audio.Assets) File.Delete(Path.Combine(source, sound.RelativePath));
        var captures = Path.Combine(repo.FullName, "artifacts", "screenshots", "export"); Directory.CreateDirectory(captures);
        var report = Path.Combine(captures, "report.json");
        var plan = Path.Combine(root, "plan.json");
        await File.WriteAllTextAsync(plan, "[{\"Kind\":\"tick\",\"Seconds\":2},{\"Kind\":\"quicksave\"},{\"Kind\":\"tick\",\"Seconds\":3},{\"Kind\":\"quickload\"},{\"Kind\":\"next\"},{\"Kind\":\"click\",\"X\":650,\"Y\":300},{\"Kind\":\"quicksave\"},{\"Kind\":\"click\",\"X\":500,\"Y\":300},{\"Kind\":\"quickload\"},{\"Kind\":\"click\",\"X\":500,\"Y\":300}]");
        var start = new ProcessStartInfo(Path.Combine(moved, "NovellMaker.Runtime.exe"))
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["DOTNET_ROOT"] = Path.Combine(root, "no-dotnet");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(root, "no-dotnet");
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var arg in new[] { "--smoke-output", Path.Combine(captures, "game.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Export smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var trace = json.RootElement.GetProperty("ActionTrace");
        Assert.AreEqual(10, trace.GetArrayLength());
        Assert.IsTrue(trace.EnumerateArray().All(s => s.GetProperty("Success").GetBoolean()));
        Assert.AreEqual(trace[1].GetProperty("State").GetRawText(), trace[3].GetProperty("State").GetRawText());
        Assert.AreEqual(trace[6].GetProperty("State").GetRawText(), trace[8].GetProperty("State").GetRawText());
        StringAssert.Contains(trace[9].GetProperty("Text").GetString()!, "Ветка успеха");
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.IsTrue(json.RootElement.GetProperty("Packaged").GetBoolean());
        Assert.IsGreaterThan(0L, json.RootElement.GetProperty("AudioSubmittedBytes").GetInt64());
        Assert.AreEqual(5, json.RootElement.GetProperty("AudioOpenedSources").GetInt32(), "Initial sound/voice/effect and two loads must restart only sound.");
        Assert.AreEqual(1, json.RootElement.GetProperty("AudioSources").GetInt32());
        Assert.AreEqual(project.Name, json.RootElement.GetProperty("WindowTitle").GetString());
        var quicksave = json.RootElement.GetProperty("SavePath").GetString()!;
        Assert.IsTrue(File.Exists(quicksave));
        Assert.IsFalse(quicksave.StartsWith(moved, StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(quicksave, Path.Combine("Games", project.Id.ToString(), "quicksave.json"));
        Assert.AreEqual(Path.Combine(moved, "System.Private.CoreLib.dll"), json.RootElement.GetProperty("RuntimeLibraryPath").GetString());
        using (var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(captures, "game.png")))
        {
            Assert.IsNotNull(bitmap);
            Assert.AreEqual(new SkiaSharp.SKColor(183, 146, 100), bitmap.GetPixel(720, 380));
        }
        // Startup failure is recorded outside the installation directory, including a usable stderr diagnosis.
        await File.WriteAllTextAsync(Path.Combine(moved, "game.novell"), "{}");
        using var failed = Process.Start(start)!;
        var errorText = failed.StandardError.ReadToEndAsync(); var outputText = failed.StandardOutput.ReadToEndAsync();
        await failed.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(1, failed.ExitCode);
        StringAssert.Contains(await errorText, "Ошибка runtime"); await outputText;
        var log = Path.Combine(Path.GetDirectoryName(quicksave)!, "startup-error.log");
        Assert.IsTrue(File.Exists(log));
        StringAssert.Contains(await File.ReadAllTextAsync(log), "Exception");
    }
}


