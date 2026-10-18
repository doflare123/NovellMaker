namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeArgumentsTests
{
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
