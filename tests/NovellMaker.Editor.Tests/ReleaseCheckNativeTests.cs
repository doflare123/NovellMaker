using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReleaseCheckNativeTests
{
    [TestMethod]
    [TestCategory("NativeRuntime")]
    [DataRow(false, "live")] [DataRow(true, "live")]
    [DataRow(false, "offline")] [DataRow(true, "offline")]
    [DataRow(false, "cancel")] [DataRow(true, "cancel")]
    [DataRow(false, "close")] [DataRow(true, "close")]
    [DataRow(false, "session")] [DataRow(true, "session")]
    [DataRow(false, "timeout")] [DataRow(true, "timeout")]
    public async Task AuthorReleaseMenuSendsRequestAndRemainsResponsiveInF6AndExport(bool packaged, string scenario)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" ||
            packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        if (scenario == "live" && Environment.GetEnvironmentVariable("NOVELLMAKER_RELEASE_LIVE_SMOKE") != "1")
            Assert.Inconclusive("The live case requires explicit NOVELLMAKER_RELEASE_LIVE_SMOKE=1.");

        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "release-check", packaged ? "export" : "preview", scenario);
        Directory.CreateDirectory(directory);
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Игра доступна во время сетевых ошибок.")] };
        var project = ExampleReleaseCheckModule.AddTo(ExampleGameMenusModule.AddTo(
            NovelProject.Create("Проверка выпуска") with { Scenes = [story] }, story.Id)) with
        { ReleaseCheck = new(ReleaseCheckConfiguration.Provider, "dotnet", "runtime", "1.0.0") };
        var file = Path.Combine(directory, "release.novell"); await new JsonProjectStore().SaveAsync(file, project);
        var runtime = Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", "Release", "net10.0", "NovellMaker.Runtime.exe");
        if (packaged)
        {
            var output = Path.Combine(directory, "game-" + Guid.NewGuid().ToString("N"));
            await new WindowsExportService(Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64"), new ImageAssetService())
                .ExportAsync(project, directory, story.Id, output);
            runtime = Path.Combine(output, "NovellMaker.Runtime.exe");
        }
        // Real sockets and the production handler. A CONNECT proxy delays the network, never fabricates release JSON.
        await using var proxy = new NetworkGate(scenario == "live", scenario == "timeout" ? 12 : 2);
        var steps = new List<object> { new { Kind = "click", X = 630, Y = 230 }, new { Kind = "key", Key = "F5" },
            new { Kind = "click", X = 240, Y = 470 }, new { Kind = "wait_now", Seconds = .3 }, new { Kind = "click", X = 240, Y = 570 } };
        switch (scenario)
        {
            case "cancel": steps.Add(new { Kind = "click", X = 550, Y = 470 }); steps.Add(new { Kind = "wait_release" }); steps.Add(new { Kind = "wait_now", Seconds = 3 }); break;
            case "close": steps.Add(new { Kind = "click", X = 850, Y = 470 }); steps.Add(new { Kind = "key", Key = "F5" }); steps.Add(new { Kind = "wait_now", Seconds = 3 }); break;
            case "session": steps.Add(new { Kind = "key", Key = "F2" }); steps.Add(new { Kind = "click", X = 630, Y = 230 }); steps.Add(new { Kind = "key", Key = "F5" }); steps.Add(new { Kind = "wait_now", Seconds = 3 }); break;
            default: steps.Add(new { Kind = "wait_release" }); break;
        }
        if (scenario == "live")
        {
            steps.Add(new { Kind = "click", X = 240, Y = 470 }); steps.Add(new { Kind = "wait_release" });
        }
        var plan = Path.Combine(directory, "plan.json"); File.WriteAllText(plan, JsonSerializer.Serialize(steps));
        var report = Path.Combine(directory, "report.json");
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (!packaged) foreach (var arg in new[] { "--project", file, "--assets-root", directory, "--scene", story.Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        else
        {
            start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent"); start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent");
            start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        }
        start.Environment["HTTPS_PROXY"] = proxy.Address; start.Environment["HTTP_PROXY"] = proxy.Address;
        start.Environment["ALL_PROXY"] = proxy.Address; start.Environment["NO_PROXY"] = "";
        foreach (var arg in new[] { "--smoke-output", Path.Combine(directory, "result.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Release menu smoke timed out: " + scenario); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(File.ReadAllText(report)); var result = json.RootElement; var trace = result.GetProperty("ActionTrace");
        var diagnostics = result.GetProperty("Diagnostics").ToString() + result.GetProperty("ScreenLuaDiagnostics");
        Assert.AreEqual(0, result.GetProperty("Diagnostics").GetArrayLength(), diagnostics);
        Assert.AreEqual(0, result.GetProperty("ScreenLuaDiagnostics").GetArrayLength(), diagnostics);
        Assert.IsTrue(trace.EnumerateArray().All(t => t.GetProperty("Success").GetBoolean()), trace.ToString());
        bool Text(int index, string expected) => trace[index].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == expected);
        Assert.IsTrue(trace[3].GetProperty("PendingRelease").GetBoolean(), "The observation must occur during actual transport.");
        Assert.IsTrue(trace[4].GetProperty("PendingRelease").GetBoolean(), "Input must commit while HTTP is pending.");
        Assert.IsTrue(Text(4, "Нажатий: 1")); Assert.IsTrue(Text(4, "Проверка…"));
        Assert.IsGreaterThan(trace[2].GetProperty("DrawFrames").GetInt32(), trace[3].GetProperty("DrawFrames").GetInt32());
        Assert.IsGreaterThan(0, result.GetProperty("PendingReleaseDrawFrames").GetInt32());
        Assert.AreEqual(0, trace[trace.GetArrayLength() - 1].GetProperty("ReleaseReservations").GetInt32());
        Assert.AreEqual(1, proxy.Connections, "One explicit check, no automatic retry; the second live check must use cache.");
        var last = trace.GetArrayLength() - 1;
        if (scenario == "live")
        {
            Assert.IsTrue(Text(5, "Доступна новая версия"));
            var details = trace[5].GetProperty("ScreenTexts").EnumerateArray().Select(t => t.GetString()).Single(t => t!.StartsWith("https://"))!;
            StringAssert.Contains(details, "https://github.com/dotnet/runtime/releases/tag/");
            Assert.IsTrue(trace[last].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString()!.Contains("Из кэша")));
        }
        else if (scenario is "close" or "session")
        {
            Assert.IsTrue(Text(last, "Готово к проверке")); Assert.IsTrue(Text(last, "Нажатий: 0"));
            Assert.AreEqual(trace[last - 1].GetProperty("ScreenLuaAppliedBatches").GetInt32(), trace[last].GetProperty("ScreenLuaAppliedBatches").GetInt32(), "No old callback batch after reopening.");
            var oldId = trace[4].GetProperty("ScreenInstanceIds").EnumerateArray().Last().GetGuid();
            Assert.AreNotEqual(oldId, trace[last].GetProperty("ScreenInstanceIds").EnumerateArray().Last().GetGuid());
            if (scenario == "session") Assert.IsGreaterThan(trace[4].GetProperty("SessionGeneration").GetInt64(), trace[last].GetProperty("SessionGeneration").GetInt64());
        }
        else Assert.IsTrue(Text(last, scenario == "cancel" ? "Проверка отменена" : "Ошибка проверки: " + (scenario == "timeout" ? "Timeout" : "NetworkUnavailable")), trace.ToString());
    }

    /// <summary>Loopback-only failure/delay gate. Live mode tunnels TLS unchanged to the one allowed host.</summary>
    private sealed class NetworkGate : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        private int connections;
        // The gate serves one tunnel. Any attempted retry creates another pending connection.
        internal int Connections => Volatile.Read(ref connections) + (listener.Pending() ? 1 : 0);
        internal string Address { get; }
        internal NetworkGate(bool live, int delaySeconds)
        {
            listener.Start(); Address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            run = Run(live, delaySeconds);
        }
        private async Task Run(bool live, int seconds)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream(); var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 4096 && await stream.ReadAsync(one, stop.Token) != 0)
                {
                    bytes.Add(one[0]);
                    if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                }
                var connect = Encoding.ASCII.GetString(bytes.ToArray());
                Assert.IsTrue(connect.StartsWith("CONNECT api.github.com:443 HTTP/1.1\r\n", StringComparison.Ordinal), connect);
                Interlocked.Increment(ref connections);
                await Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token);
                if (!live)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"), stop.Token); return;
                }
                using var upstream = new TcpClient(); await upstream.ConnectAsync("api.github.com", 443, stop.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), stop.Token);
                var remote = upstream.GetStream();
                var upload = stream.CopyToAsync(remote, stop.Token); var download = remote.CopyToAsync(stream, stop.Token);
                await Task.WhenAny(upload, download); await stop.CancelAsync();
                await Task.WhenAll(upload, download);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException && stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync(); listener.Stop();
            try { await run; } finally { stop.Dispose(); }
        }
    }
}
