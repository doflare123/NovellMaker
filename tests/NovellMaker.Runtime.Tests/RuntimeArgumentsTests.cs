namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeArgumentsTests
{
    [TestMethod]
    public void OrdinarySmokeReportSerializesItsMissingMinigameAsExplicitLuaNull()
    {
        var report = new RuntimeSmokeReport(true, 1, null, null, null, false, false, false, 0, 0, 0, []);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(report));
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("MinigameState").ValueKind);
    }
    [TestMethod]
    [DataRow("1")] [DataRow("5")] [DataRow("30")]
    public void RealtimeProbeUsesBoundedRealDurationWithoutAnActionPlan(string duration)
    {
        var file = Path.Combine(temporaryDirectory, "probe.novell"); File.WriteAllText(file, "{}");
        var args = RuntimeArguments.Parse(["--project", file, "--assets-root", temporaryDirectory, "--scene", Guid.NewGuid().ToString(), "--language", "ru",
            "--smoke-output", Path.Combine(temporaryDirectory, "probe.png"), "--smoke-report", Path.Combine(temporaryDirectory, "probe.json"), "--smoke-realtime-seconds", duration]);
        Assert.AreEqual(double.Parse(duration), args.SmokeRealtimeDuration!.Value.TotalSeconds); Assert.IsNull(args.SmokePlanPath);
    }

    [TestMethod]
    [DataRow("0")] [DataRow("31")] [DataRow("NaN")] [DataRow("Infinity")] [DataRow("bad")]
    [DataRow("missing-output")] [DataRow("with-plan")]
    public void RealtimeProbeRejectsInvalidOrMixedModes(string mutation)
    {
        var file = Path.Combine(temporaryDirectory, "probe.novell"); File.WriteAllText(file, "{}");
        var args = new List<string> { "--project", file, "--assets-root", temporaryDirectory, "--scene", Guid.NewGuid().ToString(), "--language", "ru" };
        if (mutation != "missing-output") args.AddRange(["--smoke-output", Path.Combine(temporaryDirectory, "probe.png"), "--smoke-report", Path.Combine(temporaryDirectory, "probe.json")]);
        if (mutation == "with-plan") args.AddRange(["--smoke-plan", file]);
        args.AddRange(["--smoke-realtime-seconds", mutation is "missing-output" or "with-plan" ? "5" : mutation]);
        Assert.Throws<ArgumentException>(() => RuntimeArguments.Parse(args));
    }
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "NovellMaker.Runtime.Tests",
        Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(temporaryDirectory);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PackagedLaunchUsesExecutableDirectoryAndManifestRatherThanWorkingDirectory()
    {
        var package = new NovellMaker.Core.Projects.GamePackage(1, Guid.NewGuid(), Guid.NewGuid(), "ru", "Игра");
        await package.SaveAsync(temporaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "game.novell"), "{}");
        var result = RuntimeArguments.Parse([], temporaryDirectory);
        Assert.AreEqual(package, result.Package);
        Assert.AreEqual(package.StartSceneId, result.SceneId);
        Assert.AreEqual(temporaryDirectory, result.AssetsRoot);
        Assert.IsNull(result.LineId);
        Assert.AreNotEqual(RuntimeStorage.SavePath(package.ProjectId, false), RuntimeStorage.SavePath(package.ProjectId, true));
        Assert.AreNotEqual(RuntimeStorage.SlotsDirectory(package.ProjectId, false), RuntimeStorage.SlotsDirectory(package.ProjectId, true));
        StringAssert.Contains(RuntimeStorage.SlotsDirectory(package.ProjectId, false), Path.Combine("Saves", package.ProjectId.ToString(), "slots"));
        Assert.AreEqual(Path.Combine(RuntimeStorage.GameDirectory(package.ProjectId), "slots"), RuntimeStorage.SlotsDirectory(package.ProjectId, true));
        StringAssert.Contains(RuntimeStorage.SavePath(package.ProjectId, true), package.ProjectId.ToString());
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "game.json"), "{\"Version\":99}");
        Assert.Throws<Exception>(() => RuntimeArguments.Parse([], temporaryDirectory));
    }

    [TestMethod]
    public void Parse_ValidInvocation_NormalizesPathsAndKeepsStableIds()
    {
        var projectPath = Path.Combine(temporaryDirectory, "preview.novell");
        File.WriteAllText(projectPath, "{}");
        var sceneId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var screenshotPath = Path.Combine(temporaryDirectory, "artifacts", "scene.png");
        var reportPath = Path.Combine(temporaryDirectory, "artifacts", "scene.json");

        var result = RuntimeArguments.Parse(
        [
            "--project", projectPath,
            "--assets-root", temporaryDirectory,
            "--scene", sceneId.ToString(),
            "--line", lineId.ToString(),
            "--language", "ru",
            "--smoke-output", screenshotPath,
            "--smoke-report", reportPath,
        ]);

        Assert.AreEqual(Path.GetFullPath(projectPath), result.ProjectPath);
        Assert.AreEqual(Path.GetFullPath(temporaryDirectory), result.AssetsRoot);
        Assert.AreEqual(sceneId, result.SceneId);
        Assert.AreEqual(lineId, result.LineId);
        Assert.AreEqual("ru", result.Language);
        Assert.AreEqual(Path.GetFullPath(screenshotPath), result.SmokeOutputPath);
        Assert.AreEqual(Path.GetFullPath(reportPath), result.SmokeReportPath);
    }

    [TestMethod]
    public void Parse_SmokeOutputWithoutReport_IsRejectedBeforeOpeningGraphics()
    {
        var projectPath = Path.Combine(temporaryDirectory, "preview.novell");
        File.WriteAllText(projectPath, "{}");

        var error = Assert.ThrowsExactly<ArgumentException>(() => RuntimeArguments.Parse(
        [
            "--project", projectPath,
            "--assets-root", temporaryDirectory,
            "--scene", Guid.NewGuid().ToString(),
            "--language", "ru",
            "--smoke-output", Path.Combine(temporaryDirectory, "scene.png"),
        ]));

        StringAssert.Contains(error.Message, "одновременно");
    }

    [TestMethod]
    public void Parse_UnknownOrDuplicateOptions_AreRejected()
    {
        var unknown = Assert.ThrowsExactly<ArgumentException>(() => RuntimeArguments.Parse(
        ["--unknown", "value"]));
        StringAssert.Contains(unknown.Message, "Неизвестный");

        var duplicate = Assert.ThrowsExactly<ArgumentException>(() => RuntimeArguments.Parse(
        ["--project", "one", "--project", "two"]));
        StringAssert.Contains(duplicate.Message, "более одного раза");
    }

    [TestMethod]
    public void Parse_MissingSnapshotOrAssetsRoot_IsRejected()
    {
        var missingProject = Assert.ThrowsExactly<ArgumentException>(() => RuntimeArguments.Parse(
        [
            "--project", Path.Combine(temporaryDirectory, "missing.novell"),
            "--assets-root", temporaryDirectory,
            "--scene", Guid.NewGuid().ToString(),
            "--language", "ru",
        ]));
        StringAssert.Contains(missingProject.Message, "не найден");

        var projectPath = Path.Combine(temporaryDirectory, "preview.novell");
        File.WriteAllText(projectPath, "{}");
        var missingRoot = Assert.ThrowsExactly<ArgumentException>(() => RuntimeArguments.Parse(
        [
            "--project", projectPath,
            "--assets-root", Path.Combine(temporaryDirectory, "missing-root"),
            "--scene", Guid.NewGuid().ToString(),
            "--language", "ru",
        ]));
        StringAssert.Contains(missingRoot.Message, "не найден");
    }
}
