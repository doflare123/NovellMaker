using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

/// <summary>Opt-in diagnostic measurement, with no machine-dependent timing assertions.</summary>
[TestClass]
public sealed class LuaWorkerMeasurementTests
{
    [TestMethod]
    [TestCategory("LuaMeasurement")]
    public void MeasureFreshWorkerRoundTripsForSmallAndLargeProjects()
    {
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_LUA_BENCHMARK_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Enable scripts/check.ps1 -MeasureLua.");
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        foreach (var (label, lineCount, textLength) in new[] { ("small", 2, 32), ("large", 1500, 256) })
        {
            var scene = NovelScene.Create("Measurement") with { Lines = [.. Enumerable.Range(0, lineCount)
                .Select(_ => DialogueLine.Create("ru", new string('a', textLength)))] };
            var project = NovelProject.Create("Measurement") with { Scenes = [scene] };
            var state = new ScenarioPlayer(project, scene.Id, "ru").State;
            var action = new ScriptAction(Guid.NewGuid(), "game.set('measured', 'yes')", "measure.lua");
            var request = new LuaWorkerRequest("run", action.Source, action.Name, action, state, project);
            // Same serializer and request shape as the production client. Production timing below
            // includes serialization, process startup, IPC, VM execution and parent validation.
            var timer = Stopwatch.StartNew();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request).Length;
            var serializationMs = timer.Elapsed.TotalMilliseconds;
            Assert.IsLessThan(LuaWorkerClient.MaximumRequestBytes, bytes);
            var samples = new List<double>();
            for (var i = 0; i < 12; i++)
            {
                timer.Restart();
                var commands = LuaActionScript.Run(action, state, project);
                samples.Add(timer.Elapsed.TotalMilliseconds);
                Assert.HasCount(1, commands);
                Assert.AreEqual("yes", ((SetAction)commands[0]).Value);
            }
            var sorted = samples.Order().ToArray();
            cases.Add(new { Label = label, LineCount = lineCount, TextCharactersPerLine = textLength,
                RequestBytes = bytes, SerializationMilliseconds = serializationMs, SamplesMilliseconds = samples,
                FirstMilliseconds = samples[0], MedianMilliseconds = (sorted[5] + sorted[6]) / 2,
                P95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1] });
        }
        File.WriteAllText(Path.Combine(directory, "worker-roundtrips.json"), JsonSerializer.Serialize(new
        {
            MeasuredAtUtc = DateTimeOffset.UtcNow, OS = RuntimeInformation.OSDescription,
            Framework = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount, SamplesPerCase = 12,
            Note = "Each call starts a fresh process. OS disk caches are not flushed. Includes serialization and parent validation; not a pure process-start benchmark. Run without coverage and parallel benchmarks.",
            Cases = cases
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
