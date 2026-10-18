using System.Diagnostics;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services.Preview;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PreviewServiceTests
{
    [TestMethod]
    public async Task RuntimeStartupErrorReportsFailureAndRemovesItsSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovellMaker.Preview.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new PreviewService(RuntimePath(), root, hidden: true);
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var request = new PreviewRequest(NovelProject.Create("Ошибка"), root, Guid.NewGuid(), null, "ru");
            try { await service.StartAsync(request, messages.Enqueue); }
            catch (IOException) { }
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue(messages.Any(message => message.Contains("кодом 1") && message.Contains("отсутствует")), string.Join("\n", messages));
            Assert.IsNull(service.ActiveProcessId);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task RestartAndDisposeStopOnlyOwnedRuntimeAndCleanSnapshots()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1")
            Assert.Inconclusive("Set NOVELLMAKER_NATIVE_SMOKE=1 for the native preview process lifecycle test.");
        var root = Path.Combine(Path.GetTempPath(), "NovellMaker.Preview.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var service = new PreviewService(RuntimePath(), root, hidden: true);
        try
        {
            var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Проверка")] };
            var project = NovelProject.Create("Снимок") with { Scenes = [scene] };
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var request = new PreviewRequest(project, root, scene.Id, scene.Lines[0].Id, "ru");
            await service.StartAsync(request, messages.Enqueue);
            var firstId = service.ActiveProcessId;
            Assert.IsNotNull(firstId);
            Assert.IsTrue(IsAlive(firstId.Value));
            var firstCompletion = service.Completion;
            var firstSnapshot = Directory.GetFiles(root, "snapshot.novell", SearchOption.AllDirectories).Single();
            Assert.IsTrue(project.ContentEquals(await new JsonProjectStore().LoadAsync(firstSnapshot)));

            await service.StartAsync(request, messages.Enqueue);
            await firstCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(IsAlive(firstId.Value));
            Assert.IsFalse(File.Exists(firstSnapshot));
            var secondId = service.ActiveProcessId;
            Assert.IsNotNull(secondId);
            Assert.AreNotEqual(firstId, secondId);
            Assert.IsTrue(IsAlive(secondId.Value));
            Assert.AreEqual(1, Directory.GetFiles(root, "snapshot.novell", SearchOption.AllDirectories).Length);
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(IsAlive(secondId.Value));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
            Assert.IsFalse(messages.Any(message => message.Contains("кодом")), "Intentional replacement/close must not report a runtime failure.");
        }
        finally
        {
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Directory.Delete(root, true);
        }
    }

    private static bool IsAlive(int id)
    {
        try { using var child = Process.GetProcessById(id); return !child.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string RuntimePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent!.Name;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NovellMaker.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var executable = OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime";
        var path = Path.Combine(directory.FullName, "src", "NovellMaker.Editor", "bin", configuration, "net10.0", "Runtime", executable);
        Assert.IsTrue(File.Exists(path), $"Editor distribution is missing its runtime: {path}");
        return path;
    }
}
