using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CharacterAnimationTests
{
    [TestMethod]
    public async Task ExampleRoundTripVisualEditsUndoAndPreviewSnapshotUseSameRig()
    {
        var output = Environment.GetEnvironmentVariable("NOVELLMAKER_CHARACTER_EXAMPLE_DIR");
        var directory = output ?? Path.Combine(Path.GetTempPath(), "NovellMaker.CharacterAnimation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = ExampleCharacterAnimationModule.AddTo(NovelProject.Create("Персонаж из частей"), directory);
            var path = Path.Combine(directory, "example.novell"); await new JsonProjectStore().SaveAsync(path, project);
            using var session = new EditorSession(new JsonProjectStore(), new Dialogs(path), new Git());
            await session.OpenProjectAsync(); session.SelectCharacter(project.Characters[0].Id);
            var original = session.SelectedState!.Model.Visual!;
            var changed = original with { Parts = original.Parts.SetItem(2, original.Parts[2] with { PivotX = 15 }) };
            session.SetCharacterVisual(changed); Assert.IsTrue(session.IsDirty);
            Assert.AreEqual(15, session.SelectedState.Model.Visual!.Parts[2].PivotX);
            session.Undo(); Assert.IsTrue(original.ContentEquals(session.SelectedState.Model.Visual));
            session.Redo(); Assert.IsTrue(changed.ContentEquals(session.SelectedState.Model.Visual));
            using var preview = new ScenePlayer(session.Project!, project.Scenes[0].Id, "ru");
            preview.Tick(TimeSpan.FromSeconds(.75));
            Assert.AreEqual(3, preview.Frame.Characters[0].Sprites.Length);
            Assert.IsTrue(changed.ContentEquals(preview.Frame.Characters[0].Visual));
            // The active preview owns an immutable snapshot; further edits are visible on the next F6.
            session.SetCharacterVisual(original); Assert.IsTrue(changed.ContentEquals(preview.Frame.Characters[0].Visual));
        }
        finally { if (output is null && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task GifExtractsCompositedFramesAndDurationsAndRejectsTruncationWithoutCommittedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.Gif", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            // Two complete, independent 1x1 frames: 100 ms red, 300 ms blue, infinite loop.
            var bytes = Convert.FromHexString("47494638396101000100800000FF00000000FF21FF0B4E45545343415045322E30030100000021F904000A0000002C000000000100010000020244010021F904001E0000002C00000000010001000002024C01003B");
            var source = Path.Combine(directory, "two.gif"); await File.WriteAllBytesAsync(source, bytes);
            var result = await CharacterGifImporter.ImportAsync(directory, source);
            Assert.AreEqual(2, result.Images.Length); Assert.AreEqual(.1, result.Clip.Frames[0].Duration); Assert.AreEqual(.3, result.Clip.Frames[1].Duration);
            Assert.IsTrue(result.Clip.Loop);
            using var red = SKBitmap.Decode(Path.Combine(directory, result.Images[0].RelativePath));
            using var blue = SKBitmap.Decode(Path.Combine(directory, result.Images[1].RelativePath));
            Assert.AreEqual(SKColors.Red, red.GetPixel(0, 0)); Assert.AreEqual(SKColors.Blue, blue.GetPixel(0, 0));
            var count = Directory.GetFiles(Path.Combine(directory, "Assets", "Images")).Length;
            await File.WriteAllBytesAsync(source, bytes[..^7]);
            await Assert.ThrowsAsync<ArgumentException>(() => CharacterGifImporter.ImportAsync(directory, source));
            Assert.AreEqual(count, Directory.GetFiles(Path.Combine(directory, "Assets", "Images")).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task GifComposesPartialUpdatesAndRestorePreviousDisposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.GifDisposal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "partial.gif");
            await File.WriteAllBytesAsync(source, Convert.FromHexString(
                "47494638396102000100810000FF00000000FF00FF00000000" +
                "21F904000A0000002C0000000002000100000202040A00" +
                "21F9040C140000002C00000000010001000002024C0100" +
                "21F904001E0000002C01000000010001000002025401003B"));
            var imported = await CharacterGifImporter.ImportAsync(directory, source);
            Assert.AreEqual(3, imported.Images.Length);
            using var first = SKBitmap.Decode(Path.Combine(directory, imported.Images[0].RelativePath));
            using var second = SKBitmap.Decode(Path.Combine(directory, imported.Images[1].RelativePath));
            using var third = SKBitmap.Decode(Path.Combine(directory, imported.Images[2].RelativePath));
            Assert.AreEqual(SKColors.Red, first.GetPixel(1, 0));
            Assert.AreEqual(SKColors.Blue, second.GetPixel(0, 0)); Assert.AreEqual(SKColors.Red, second.GetPixel(1, 0));
            Assert.AreEqual(SKColors.Red, third.GetPixel(0, 0), "RestorePrevious removes the blue update.");
            Assert.AreEqual(SKColors.Lime, third.GetPixel(1, 0));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [TestCategory("Delivery")]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame(bool packaged, bool flipbook)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = NativeDelivery.Evidence(repo!.FullName, "character-animation", (packaged ? "export" : "preview") + (flipbook ? "-frames" : "-parts"));
        Directory.CreateDirectory(directory);
        var project = ExampleCharacterAnimationModule.AddTo(NovelProject.Create("Персонаж из частей"), directory);
        var scene = project.Scenes[0];
        var path = Path.Combine(directory, "example.novell"); await new JsonProjectStore().SaveAsync(path, project);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var runtime = NativeDelivery.Preview(Path.Combine(repo.FullName, "src", "NovellMaker.Editor", "bin", configuration, "net10.0", "Runtime", "NovellMaker.Runtime.exe"));
        if (packaged)
        {
            var game = Path.Combine(directory, "game");
            await new WindowsExportService(NativeDelivery.Template(repo.FullName), new ImageAssetService()).ExportAsync(project, directory, scene.Id, game);
            runtime = Path.Combine(game, "NovellMaker.Runtime.exe");
        }
        var plan = Path.Combine(directory, "plan.json");
        await File.WriteAllTextAsync(plan, "[" + (flipbook ? "{\"Kind\":\"next\"}," : "") + """
            {"Kind":"tick","Seconds":0.75},{"Kind":"save"},{"Kind":"tick","Seconds":0.5},{"Kind":"load"}]
            """);
        var report = Path.Combine(directory, "report.json"); var screenshot = Path.Combine(directory, "frame.png");
        var start = new ProcessStartInfo(runtime) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        if (!packaged) foreach (var arg in new[] { "--project", path, "--assets-root", directory, "--scene", scene.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        NativeDelivery.Isolate(start, directory, packaged);
        foreach (var arg in new[] { "--smoke-output", screenshot, "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Character native smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var trace = json.RootElement.GetProperty("ActionTrace");
        var saved = trace[flipbook ? 2 : 1]; var restored = trace[flipbook ? 4 : 3];
        Assert.AreEqual(saved.GetProperty("State").GetRawText(), restored.GetProperty("State").GetRawText());
        Assert.AreEqual(saved.GetProperty("CharacterSprites").GetRawText(), restored.GetProperty("CharacterSprites").GetRawText());
        Assert.IsTrue(trace.EnumerateArray().All(t => t.GetProperty("Success").GetBoolean()));
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.AreEqual(flipbook ? 1 : 3, restored.GetProperty("CharacterSprites").GetArrayLength());
        using var frame = SKBitmap.Decode(screenshot);
        Assert.AreEqual(new SKColor(77, 119, 151), frame.GetPixel(640, 400), "The actual MonoGame frame contains the body.");
        Assert.AreEqual(new SKColor(224, 197, 166), frame.GetPixel(640, 300), "The actual MonoGame frame contains the child head.");
    }

    private sealed class Dialogs(string path) : IEditorDialogs
    {
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveProjectAsync(string name) => Task.FromResult<string?>(path);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message) => throw new AssertFailedException(title + ": " + message);
    }
    private sealed class Git : IGitStatusService { public Task<GitStatusSnapshot> GetStatusAsync(string path, CancellationToken token = default) => Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Нет репозитория")); }
}
