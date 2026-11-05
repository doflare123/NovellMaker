using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class PublishedWorkerNativeTests
{
    [TestMethod]
    [TestCategory("NativePortability")]
    public async Task PublishedWorkerExecutesCommandsAndRecoversAfterInstructionBudgetFailure()
    {
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_RUNTIME_DIR");
        if (directory is null) Assert.Inconclusive("Run scripts/check-native-portability.ps1 with a self-contained runtime.");
        var scene = NovelScene.Create("Portable worker");
        var project = NovelProject.Create("Portable worker") with { Scenes = [scene] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru");
        var action = new ScriptAction(Guid.NewGuid(), "game.set('portable', 'yes')", "portable.lua");
        var request = new LuaWorkerRequest("run", action.Source, action.Name, action,
            LuaStateSnapshot.Capture(player.State), LuaCommandCatalog.Capture(project, scene.Id));

        var success = await Invoke(request);
        Assert.IsNull(success.Error, success.Error);
        Assert.AreEqual("yes", ((SetAction)success.Commands.Single()).Value);
        var looping = action with { Source = "game.set('portable', 'partial'); while true do end" };
        var failure = await Invoke(request with { Source = looping.Source, Action = looping });
        Assert.IsNotNull(failure.Error);
        StringAssert.Contains(failure.Error, "бюджет");
        Assert.IsEmpty(failure.Commands, "A failed invocation must not return partial commands.");
        success = await Invoke(request);
        Assert.IsNull(success.Error, success.Error);
        Assert.AreEqual("yes", ((SetAction)success.Commands.Single()).Value);

        async Task<LuaWorkerResponse> Invoke(LuaWorkerRequest input)
        {
            var start = new ProcessStartInfo(Path.Combine(directory, OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
            start.ArgumentList.Add("--lua-worker");
            start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent");
            start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
            start.Environment["DOTNET_ROOT_ARM64"] = start.Environment["DOTNET_ROOT"];
            start.Environment["PATH"] = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : "/usr/bin:/bin";
            start.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000";
            start.Environment["COMPlus_GCHeapHardLimit"] = "0x4000000";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, input, cancellationToken: timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                Assert.AreEqual(0, process.ExitCode, await stderr);
                return JsonSerializer.Deserialize<LuaWorkerResponse>(await stdout)!;
            }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            }
        }
    }
}
