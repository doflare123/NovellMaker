using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class GameSaveMeasurementTests
{
    [TestMethod]
    [TestCategory("FramePreparationMeasurement")]
    public async Task MeasurePreparedLoadAndHostCommitOnLargeNovel()
    {
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_FRAME_BENCHMARK_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Enable scripts/check.ps1 -MeasureFramePreparation.");
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        foreach (var (sceneCount, lineCount) in new[] { (1, 2), (20, 500) })
        {
            var project = NovelProject.Create("Load measurement") with { Scenes = [.. Enumerable.Range(0, sceneCount).Select(i =>
                NovelScene.Create("Scene " + i) with { Lines = [.. Enumerable.Range(0, lineCount).Select(_ => DialogueLine.Create("ru", new string('a', 256)))] })] };
            using var session = new GameSession(project, project.Scenes[0].Id, "ru");
            var json = GameSave.Serialize(session.Player.Execution);
            var previous = new List<double>(); var preparation = new List<double>(); var commit = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                var timer = Stopwatch.StartNew(); session.LoadSave(json); var oldMs = timer.Elapsed.TotalMilliseconds;
                var context = session.CaptureLoadPreparation(); var generation = session.Generation;
                timer.Restart(); using var prepared = await context.PrepareAsync(json, CancellationToken.None);
                var prepMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart(); var applied = session.TryLoadPrepared(prepared, generation); var commitMs = timer.Elapsed.TotalMilliseconds;
                Assert.IsTrue(applied); Assert.AreEqual(json, GameSave.Serialize(session.Player.Execution));
                if (i >= 3) { previous.Add(oldMs); preparation.Add(prepMs); commit.Add(commitMs); }
            }
            object Summary(List<double> samples)
            {
                var sorted = samples.Order().ToArray();
                return new { MedianMilliseconds = (sorted[5] + sorted[6]) / 2, P95Milliseconds = sorted[^1], SamplesMilliseconds = samples };
            }
            cases.Add(new { Scenes = sceneCount, Lines = sceneCount * lineCount, PreviousHostLoad = Summary(previous),
                BackgroundPreparation = Summary(preparation), ReadyHostCommit = Summary(commit) });
        }
        File.WriteAllText(Path.Combine(directory, "prepared-load.json"), JsonSerializer.Serialize(new {
            MeasuredAtUtc = DateTimeOffset.UtcNow, Framework = RuntimeInformation.FrameworkDescription,
            Note = "Three warmups/twelve samples. Previous path includes dormant player construction, compilation, fingerprint, Restore validation and frame. Prepared path does that off-host; ready commit includes lifetime retirement/swap with no host subscribers. Excludes disk, graphics/audio/screens cleanup and drawing. Not a frame-time guarantee.",
            Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [TestMethod]
    [TestCategory("FramePreparationMeasurement")]
    public void MeasureCaptureAndPreviousSynchronousPreparationOnLargeNovel()
    {
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_FRAME_BENCHMARK_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Enable scripts/check.ps1 -MeasureFramePreparation.");
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        foreach (var (sceneCount, linesPerScene) in new[] { (1, 2), (20, 500) })
        {
            var project = NovelProject.Create("Capture measurement") with { Scenes = [.. Enumerable.Range(0, sceneCount)
                .Select(i => NovelScene.Create("Scene " + i) with { Lines = [.. Enumerable.Range(0, linesPerScene)
                    .Select(_ => DialogueLine.Create("ru", new string('a', 256)))] })] };
            using var session = new GameSession(project, project.Scenes[0].Id, "ru");
            var captures = new List<double>(); var previous = new List<double>(); var allocations = new List<long>();
            for (var i = 0; i < 15; i++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew(); var captured = GameSlotStore.Capture(session);
                var captureMs = timer.Elapsed.TotalMilliseconds;
                var captureBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                timer.Restart(); var json = GameSlotStore.Prepare(captured);
                var prepareMs = timer.Elapsed.TotalMilliseconds;
                Assert.IsTrue(json.SaveJson.Length > 0);
                if (i >= 3) { captures.Add(captureMs); previous.Add(prepareMs + captureMs); allocations.Add(captureBytes); }
            }
            object Summary(List<double> samples) => new { MedianMilliseconds = samples.Order().ElementAt(samples.Count / 2),
                P95Milliseconds = samples.Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1), SamplesMilliseconds = samples };
            cases.Add(new { Scenes = sceneCount, Lines = sceneCount * linesPerScene, TextCharactersPerLine = 256,
                Capture = Summary(captures), PreviousHostPreparation = Summary(previous), CaptureAllocatedBytes = allocations });
        }
        File.WriteAllText(Path.Combine(directory, "save-capture.json"), JsonSerializer.Serialize(new {
            MeasuredAtUtc = DateTimeOffset.UtcNow, Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, ProcessorCount = Environment.ProcessorCount,
            Note = "Three warmups, twelve samples; no timing assertions. Previous path = capture + validation/fingerprint/serialization; now only capture stays on host. Excludes store's second validation, disk, runtime drawing and GPU. Synthetic novel, not an FPS guarantee.",
            Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
