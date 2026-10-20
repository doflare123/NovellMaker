using System.Diagnostics;
using System.Collections.Immutable;
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
    public void MeasureReadyLuaCommitOnHostWithoutWorkerOrProjectCompilationTime()
    {
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_LUA_BENCHMARK_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Enable scripts/check.ps1 -MeasureLua.");
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        foreach (var lines in new[] { 2, 1500 })
        {
            var script = new ScriptAction(Guid.NewGuid(), "-- host commit measurement", "commit.lua");
            var scene = NovelScene.Create("Commit") with { UseActions = true,
                Lines = [.. Enumerable.Range(0, lines).Select(_ => DialogueLine.Create("ru", new string('a', 256)))],
                Actions = [script, new WaitAction(Guid.NewGuid(), "input")] };
            var project = NovelProject.Create("Commit") with { Scenes = [scene] };
            var commands = Enumerable.Range(0, 256).Select(i => (ScenarioAction)new SetAction(Guid.NewGuid(), "v" + i, "yes")).ToImmutableArray();
            var samples = new List<double>();
            for (var i = 0; i < 36; i++)
            {
                using var player = new ScenarioPlayer(project, scene.Id, "ru", (_, _, _, _) => Task.FromResult(commands));
                player.PendingLuaTask!.GetAwaiter().GetResult(); // Prepare/validate off-host before timing the ready commit.
                var timer = Stopwatch.StartNew();
                var applied = player.PollLua();
                var elapsed = timer.Elapsed.TotalMilliseconds;
                Assert.IsTrue(applied);
                Assert.HasCount(256, player.State.Variables);
                if (i >= 4) samples.Add(elapsed);
            }
            var sorted = samples.Order().ToArray();
            cases.Add(new { Lines = lines, Commands = 256, SamplesMilliseconds = samples,
                MedianMilliseconds = (sorted[15] + sorted[16]) / 2,
                P95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1] });
        }
        File.WriteAllText(Path.Combine(directory, "host-lua-commit.json"), JsonSerializer.Serialize(new {
            MeasuredAtUtc = DateTimeOffset.UtcNow, Framework = RuntimeInformation.FrameworkDescription,
            Note = "Controlled validated ready result; measures queue commit and Pump on host. Four warmups, 32 samples per case. Excludes background validation, project compilation, worker, MonoGame rendering and GPU; not an FPS guarantee.", Cases = cases
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

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
