using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.Services.Preview;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WindowsInstallerTests
{
    [TestMethod]
    [DataRow("1.2.3", "1.2.3.0")]
    [DataRow("65535.0.1.2", "65535.0.1.2")]
    public void VersionIsValidatedWithoutRequiringWindows(string value, string expected) =>
        Assert.AreEqual(expected, new WindowsInstallerOptions(value).ValidateVersion());

    [TestMethod]
    [DataRow("1.2")]
    [DataRow("65536.1.0")]
    [DataRow("1.2.3\n!system calc")]
    public void InvalidVersionsAreRejected(string value) =>
        Assert.ThrowsExactly<ArgumentException>(() => new WindowsInstallerOptions(value).ValidateVersion());

    [TestMethod]
    public void GeneratedUninstallerDeletesOnlyExplicitFilesAndLeavesSavesOutsideInstallation()
    {
        var script = WindowsInstallerScript.Create(Guid.NewGuid(), "Тест \"EXE\" $INSTDIR ${NSISDIR}", new(), ["game.json", "Assets/Images/actor.png"]);
        StringAssert.Contains(script, "RequestExecutionLevel user");
        StringAssert.Contains(script, "SetCompressor /SOLID zlib");
        StringAssert.Contains(script, "Delete \"$INSTDIR\\Assets\\Images\\actor.png\"");
        Assert.IsFalse(script.Contains("RMDir /r", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("Delete \"$LOCALAPPDATA", StringComparison.Ordinal));
        StringAssert.Contains(script, "${U+24}${U+24}INSTDIR");
        Assert.ThrowsExactly<ArgumentException>(() => WindowsInstallerScript.Create(Guid.NewGuid(), "test\n!system calc", new(), ["game.json"]));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsInstallerScript.Create(Guid.NewGuid(), "test", new(), ["../outside.txt"]));
    }

    [TestMethod]
    public async Task MissingCompilerDoesNotProduceInstallerOrChangeExistingFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "NovellMaker.Installer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = BuiltInExample.Create(Path.Combine(root, "source"));
            var service = new WindowsInstallerService(new("missing", new ImageAssetService()), Path.Combine(root, "missing"));
            var destination = Path.Combine(root, "setup.exe");
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => service.ExportAsync(project, root, project.Scenes[0].Id, destination, new()));
            Assert.IsFalse(File.Exists(destination));
            await File.WriteAllTextAsync(destination, "existing file");
            await Assert.ThrowsExactlyAsync<IOException>(() => service.ExportAsync(project, root, project.Scenes[0].Id, destination, new()));
            Assert.AreEqual("existing file", await File.ReadAllTextAsync(destination));
        }
        finally { DeleteFixture(root); }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    [TestCategory("Delivery")]
    public async Task NativeInstallerInstallsRunsGameAndUninstallsWithoutRemovingSavesOrForeignFiles()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NOVELLMAKER_INSTALLER_SMOKE") != "1")
        { Assert.Inconclusive("Set NOVELLMAKER_INSTALLER_SMOKE=1 after prepare-windows-installer.ps1 on Windows."); return; }
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var root = Path.Combine(Path.GetTempPath(), "NovellMaker.Installer.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var installed = Path.Combine(root, "Установленная игра с пробелами");
        var dialogs = new DeliveryDialogs(Path.Combine(source, "delivery.novell"));
        var distribution = Environment.GetEnvironmentVariable("NOVELLMAKER_DELIVERY_EDITOR_DIR") ??
            Path.Combine(repo.FullName, "src", "NovellMaker.Editor", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0");
        using var preview = new PreviewService(Path.Combine(distribution, "Runtime", "NovellMaker.Runtime.exe"), Path.Combine(root, "preview"), hidden: true);
        using var session = new EditorSession(new JsonProjectStore(), dialogs, new GitStatusService(), previewService: preview);
        // File -> Example creates an independent authoring project through the editor's command.
        await session.OpenExampleAsync();
        var exampleDirectory = Path.GetDirectoryName(session.ProjectPath!)!;
        session.ProjectName = "Игра \"EXE\" $INSTDIR ${NSISDIR} $%TEMP% & тест";
        await session.SaveAsync(true);
        Assert.IsEmpty(dialogs.Errors, string.Join("\n", dialogs.Errors));
        Assert.AreEqual(dialogs.Path, session.ProjectPath);
        Assert.IsFalse(session.IsDirty);
        var project = await new JsonProjectStore().LoadAsync(dialogs.Path);
        var unusual = project.Images[0] with { RelativePath = "Assets/Images/actor${NSISDIR}-$value.png" };
        File.Move(Path.Combine(source, project.Images[0].RelativePath), Path.Combine(source, unusual.RelativePath));
        project = project with { Images = project.Images.SetItem(0, unusual) };
        var id = project.Id.ToString("N");
        var key = $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NovellMaker-{id}";
        var saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Games", project.Id.ToString());
        var shortcuts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NovellMaker Games", id);
        var output = Path.Combine(repo.FullName, "artifacts", "installers"); Directory.CreateDirectory(output);
        var installer = Path.Combine(output, "NovellMaker-Example-Setup.exe");
        if (File.Exists(installer)) File.Delete(installer);
        var compiler = Path.Combine(distribution, "BuildTools", "NSIS");
        try
        {
            if (Environment.GetEnvironmentVariable("NOVELLMAKER_DELIVERY_EDITOR_DIR") is not null)
                await AssertEditorStartsAsync(distribution, root);
            await new JsonProjectStore().SaveAsync(dialogs.Path, project);
            await session.OpenProjectAsync();
            session.OpenScene(project.Scenes[0].Id);
            await session.PreviewSceneAsync();
            Assert.IsEmpty(dialogs.Errors, string.Join("\n", dialogs.Errors));
            Assert.IsNotNull(preview.ActiveProcessId);
            await Task.Delay(1000);
            Assert.IsNotNull(preview.ActiveProcessId, string.Join("\n", session.Diagnostics));
            var snapshot = Directory.GetFiles(Path.Combine(root, "preview"), "snapshot.novell", SearchOption.AllDirectories).Single();
            Assert.IsTrue(project.ContentEquals(await new JsonProjectStore().LoadAsync(snapshot)));
            preview.Dispose(); await preview.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            var exporter = new WindowsExportService(Path.Combine(distribution, "ExportTemplates", "win-x64"), new ImageAssetService());
            var service = new WindowsInstallerService(exporter, compiler);
            var options = new WindowsInstallerOptions("1.2.3", Path.Combine(compiler, "Contrib", "Graphics", "Icons", "modern-install.ico"));
            await service.ExportAsync(project, source, project.Scenes[0].Id, installer, options);
            Assert.IsGreaterThan(1000000L, new FileInfo(installer).Length);
            var version = FileVersionInfo.GetVersionInfo(installer);
            Assert.AreEqual("1.2.3.0", version.FileVersion);
            Assert.AreEqual(project.Name, version.ProductName, "NSIS macros and variables in a title must stay literal.");
            Assert.HasCount(0, Directory.GetDirectories(output, ".novell-installer-*"));
            var badIcon = Path.Combine(root, "invalid.ico"); await File.WriteAllTextAsync(badIcon, "not an icon");
            var invalidOutput = Path.Combine(root, "failed.exe");
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExportAsync(project, source, project.Scenes[0].Id, invalidOutput, new("1.0.0", badIcon)));
            Assert.IsFalse(File.Exists(invalidOutput)); Assert.HasCount(0, Directory.GetDirectories(root, ".novell-installer-*"));
            // Silent mode must enforce the same empty-directory rule as the interactive wizard.
            Directory.CreateDirectory(installed);
            await File.WriteAllTextAsync(Path.Combine(installed, "foreign.txt"), "retain me");
            Assert.AreNotEqual(0, await RunAsync(installer, ["/S", "/D=" + installed], root));
            Assert.AreEqual("retain me", await File.ReadAllTextAsync(Path.Combine(installed, "foreign.txt")));
            File.Delete(Path.Combine(installed, "foreign.txt")); Directory.Delete(installed);
            Assert.AreEqual(0, await RunAsync(installer, ["/S", "/D=" + installed], root));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "coreclr.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "game.ico")));
            var gameVersion = FileVersionInfo.GetVersionInfo(Path.Combine(installed, "NovellMaker.Runtime.exe"));
            Assert.AreEqual("1.2.3.0", gameVersion.FileVersion);
            Assert.AreEqual("1.2.3.0", gameVersion.ProductVersion);
            Assert.AreEqual(project.Name, gameVersion.ProductName);
            Assert.AreEqual(project.Name, gameVersion.FileDescription);
            ExecutableResources.AssertIcon(Path.Combine(installed, "NovellMaker.Runtime.exe"), await File.ReadAllBytesAsync(options.IconPath!));
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(installed, "Licenses", "PCG.txt")), "Copyright 2014 Melissa O'Neill");
            Assert.AreEqual(project.Id, GamePackage.Load(installed).ProjectId);
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var entry = registry.OpenSubKey(key))
            {
                Assert.IsNotNull(entry); Assert.AreEqual(project.Name, entry.GetValue("DisplayName"));
                Assert.AreEqual("1.2.3", entry.GetValue("DisplayVersion"));
                Assert.AreEqual(installed, entry.GetValue("InstallLocation"));
            }
            Assert.AreEqual(2, Directory.GetFiles(shortcuts, "*.lnk").Length);
            Assert.AreNotEqual(0, await RunAsync(installer, ["/S", "/D=" + Path.Combine(root, "second")], root), "A second installation of the same project must be rejected.");
            foreach (var image in project.Images) File.Delete(Path.Combine(source, image.RelativePath));
            var evidence = Environment.GetEnvironmentVariable("NOVELLMAKER_DELIVERY_EVIDENCE_DIR");
            var capture = evidence is null ? Path.Combine(repo.FullName, "artifacts", "screenshots", "installer") : Path.Combine(evidence, "installer");
            Directory.CreateDirectory(capture);
            var plan = Path.Combine(root, "plan.json");
            await File.WriteAllTextAsync(plan, """
                [{"Kind":"tick","Seconds":2},{"Kind":"quicksave"},{"Kind":"tick","Seconds":3},{"Kind":"quickload"}]
                """);
            var report = Path.Combine(capture, "report.json");
            Assert.AreEqual(0, await RunAsync(Path.Combine(installed, "NovellMaker.Runtime.exe"),
                ["--smoke-output", Path.Combine(capture, "game.png"), "--smoke-report", report, "--smoke-plan", plan], root));
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(report)))
            {
                var trace = json.RootElement.GetProperty("ActionTrace");
                Assert.IsTrue(trace.EnumerateArray().All(s => s.GetProperty("Success").GetBoolean()));
                Assert.AreEqual(trace[1].GetProperty("State").GetRawText(), trace[3].GetProperty("State").GetRawText());
                Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
                Assert.AreEqual(Path.Combine(installed, "System.Private.CoreLib.dll"), json.RootElement.GetProperty("RuntimeLibraryPath").GetString());
            }
            var save = await File.ReadAllBytesAsync(Path.Combine(saves, "quicksave.json"));
            await File.WriteAllTextAsync(Path.Combine(saves, "user-data.txt"), "данные игрока");
            var userData = Directory.GetFiles(saves, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(saves, p), File.ReadAllBytes);
            await File.WriteAllTextAsync(Path.Combine(installed, "foreign.txt"), "retain me");
            var uninstaller = Path.Combine(installed, "Uninstall.exe");
            Assert.AreEqual(0, await RunAsync(uninstaller, ["/S", "_?=" + installed], root));
            CollectionAssert.AreEqual(save, await File.ReadAllBytesAsync(Path.Combine(saves, "quicksave.json")));
            foreach (var data in userData) CollectionAssert.AreEqual(data.Value, await File.ReadAllBytesAsync(Path.Combine(saves, data.Key)));
            Assert.AreEqual("retain me", await File.ReadAllTextAsync(Path.Combine(installed, "foreign.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(installed, "NovellMaker.Runtime.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(installed, "Licenses", "PCG.txt")));
            Assert.IsFalse(Directory.Exists(shortcuts));
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)) Assert.IsNull(registry.OpenSubKey(key));
            // Installation after uninstall is possible; another revision still uses the same save identity.
            var reinstall = Path.Combine(root, "reinstalled");
            Assert.AreEqual(0, await RunAsync(installer, ["/S", "/D=" + reinstall], root));
            // A fresh process after reinstall must read the preserved slot, rather than create a new one.
            await File.WriteAllTextAsync(plan, """[{"Kind":"quickload"}]""");
            var reloadReport = Path.Combine(capture, "reinstalled-report.json");
            Assert.AreEqual(0, await RunAsync(Path.Combine(reinstall, "NovellMaker.Runtime.exe"),
                ["--smoke-output", Path.Combine(capture, "reinstalled.png"), "--smoke-report", reloadReport, "--smoke-plan", plan], root));
            using (var reloaded = JsonDocument.Parse(await File.ReadAllTextAsync(reloadReport)))
            using (var original = JsonDocument.Parse(await File.ReadAllTextAsync(report)))
            {
                Assert.IsTrue(reloaded.RootElement.GetProperty("ActionTrace")[0].GetProperty("Success").GetBoolean());
                Assert.AreEqual(original.RootElement.GetProperty("ActionTrace")[1].GetProperty("State").GetRawText(),
                    reloaded.RootElement.GetProperty("ActionTrace")[0].GetProperty("State").GetRawText());
            }
            Assert.AreEqual(0, await RunAsync(Path.Combine(reinstall, "Uninstall.exe"), ["/S", "_?=" + reinstall], root));
            foreach (var data in userData) CollectionAssert.AreEqual(data.Value, await File.ReadAllBytesAsync(Path.Combine(saves, data.Key)));
        }
        finally
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            registry.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            preview.Dispose(); await preview.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            DeleteFixture(root);
            var examplesRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NovellMaker", "Examples"));
            if (Path.GetDirectoryName(Path.GetFullPath(exampleDirectory)) == examplesRoot && Directory.Exists(exampleDirectory)) Directory.Delete(exampleDirectory, true);
            if (Path.GetFileName(saves) == project.Id.ToString() && Directory.Exists(saves)) Directory.Delete(saves, true);
            if (Path.GetFileName(shortcuts) == id && Directory.Exists(shortcuts)) Directory.Delete(shortcuts, true);
        }
    }

    private sealed class DeliveryDialogs(string path) : IEditorDialogs
    {
        internal string Path => path;
        internal List<string> Errors { get; } = [];
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveProjectAsync(string name) => Task.FromResult<string?>(path);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(title + ": " + message); return Task.CompletedTask; }
    }

    private static async Task AssertEditorStartsAsync(string distribution, string workingDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(distribution, "NovellMaker.Editor.exe"))
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.Environment["DOTNET_ROOT"] = Path.Combine(workingDirectory, "no-dotnet");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(workingDirectory, "no-dotnet");
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        var timer = Stopwatch.StartNew();
        try
        {
            while (!process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) return;
                await Task.Delay(100);
            }
            if (process.HasExited) Assert.Fail("Delivered editor exited before creating its window: " + await stderr + await stdout);
            Assert.Fail("Delivered editor did not create its window within 15 seconds.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await stderr; await stdout;
        }
    }

    private static async Task<int> RunAsync(string executable, string[] arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        if (arguments.Length == 2 && arguments[0] == "/S" && (arguments[1].StartsWith("/D=", StringComparison.Ordinal) || arguments[1].StartsWith("_?=", StringComparison.Ordinal)))
            start.Arguments = "/S " + arguments[1]; // NSIS explicitly requires its final directory argument unquoted.
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ROOT"] = Path.Combine(workingDirectory, "no-dotnet");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(workingDirectory, "no-dotnet");
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Installer process timed out: " + executable); }
        await stderr; await stdout;
        if (process.ExitCode != 0) Trace.WriteLine($"{executable}: {await stderr}\n{await stdout}");
        return process.ExitCode;
    }

    private static void DeleteFixture(string root)
    {
        var full = Path.GetFullPath(root);
        if (full.StartsWith(Path.Combine(Path.GetTempPath(), "NovellMaker.Installer.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            Directory.Delete(full, true);
    }
}
