using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class WindowsExportTests
{
    [TestMethod]
    [DataRow("missing-manifest")] [DataRow("version")] [DataRow("rid")] [DataRow("project-format")]
    [DataRow("game-manifest")] [DataRow("wire")] [DataRow("features")] [DataRow("hash")]
    [DataRow("length")] [DataRow("missing-file")] [DataRow("core-module")] [DataRow("runtime-module")]
    [DataRow("text-core")] [DataRow("text-exe")] [DataRow("text-native")] [DataRow("old-core")]
    [DataRow("extra-env")] [DataRow("nested-env")] [DataRow("extra-pdb")] [DataRow("empty-directory")]
    [DataRow("listed-env")] [DataRow("duplicate-path")] [DataRow("traversal")] [DataRow("unknown-property")]
    [DataRow("null-files")] [DataRow("null-features")] [DataRow("null-entry")] [DataRow("empty-path")]
    [DataRow("backslash-path")] [DataRow("invalid-hash")] [DataRow("zero-length")]
    [DataRow("config-empty")] [DataRow("config-wrong-version")] [DataRow("config-framework-dependent")]
    [DataRow("deps-target")] [DataRow("deps-missing-core")] [DataRow("deps-nonbinary")]
    [DataRow("core-wrong-assembly")]
    public async Task TemplateIntegrityAndCompatibilityFailuresHaveNoPackagingEffects(string mutation)
    {
        const string secret = "PRIVATE-TEMPLATE-CREDENTIAL";
        var path = Path.Combine(template, RuntimeTemplateManifest.FileName);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var entries = manifest["Files"]!.AsArray();
        void ChangedFile(string name, Action<string> change)
        {
            var file = Path.Combine(template, name); change(file);
            var entry = entries.Single(e => e!["Path"]!.GetValue<string>() == name)!;
            entry["Length"] = new FileInfo(file).Length;
            entry["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        }
        switch (mutation)
        {
            case "missing-manifest": File.Delete(path); break;
            case "version": manifest["Version"] = 2; break;
            case "rid": manifest["Rid"] = "linux-x64"; break;
            case "project-format": manifest["ProjectFormat"] = 19; break;
            case "game-manifest": manifest["GameManifest"] = 1; break;
            case "wire": manifest["ScreenWire"] = 17; break;
            case "features": manifest["Features"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "hash": entries[0]!["Sha256"] = new string('0', 64); break;
            case "length": entries[0]!["Length"] = 1; break;
            case "core-module": manifest["CoreModule"] = Guid.NewGuid(); break;
            case "runtime-module": manifest["RuntimeModule"] = Guid.NewGuid(); break;
            case "missing-file": File.Delete(Path.Combine(template, "coreclr.dll")); break;
            case "text-core": ChangedFile("NovellMaker.Core.dll", p => File.WriteAllText(p, secret)); break;
            case "text-exe": ChangedFile("NovellMaker.Runtime.exe", p => File.WriteAllText(p, secret)); break;
            case "text-native": ChangedFile("SDL2.dll", p => File.WriteAllText(p, secret)); break;
            case "old-core": ChangedFile("NovellMaker.Core.dll", RuntimeTemplateFixture.WriteOldCore); break;
            case "extra-env": File.WriteAllText(Path.Combine(template, ".env"), secret); break;
            case "nested-env": File.WriteAllText(Path.Combine(template, "Licenses", ".env"), secret); break;
            case "extra-pdb": File.WriteAllText(Path.Combine(template, "private.pdb"), secret); break;
            case "empty-directory": Directory.CreateDirectory(Path.Combine(template, "unexpected")); break;
            case "listed-env":
                var bytes = System.Text.Encoding.UTF8.GetBytes(secret); File.WriteAllBytes(Path.Combine(template, ".env"), bytes);
                entries.Add(new System.Text.Json.Nodes.JsonObject { ["Path"] = ".env", ["Length"] = bytes.Length,
                    ["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) }); break;
            case "duplicate-path": entries.Add(entries[0]!.DeepClone()); break;
            case "traversal": entries[0]!["Path"] = "../outside.dll"; break;
            case "unknown-property": manifest[secret] = secret; break;
            case "null-files": manifest["Files"] = null; break;
            case "null-features": manifest["Features"] = null; break;
            case "null-entry": entries[0] = null; break;
            case "empty-path": entries[0]!["Path"] = ""; break;
            case "backslash-path": entries[0]!["Path"] = "Licenses\\PCG.txt"; break;
            case "invalid-hash": entries[0]!["Sha256"] = new string('z', 64); break;
            case "zero-length": entries[0]!["Length"] = 0; break;
            case "config-empty": ChangedFile("NovellMaker.Runtime.runtimeconfig.json", p => File.WriteAllText(p, """{"runtimeOptions":{"includedFrameworks":[]}}""")); break;
            case "config-wrong-version": ChangedFile("NovellMaker.Runtime.runtimeconfig.json", p => File.WriteAllText(p, RuntimeTemplateFixture.Config.Replace("10.0.8", "9.0.1", StringComparison.Ordinal))); break;
            case "config-framework-dependent": ChangedFile("NovellMaker.Runtime.runtimeconfig.json", p => File.WriteAllText(p, """{"runtimeOptions":{"framework":{}}}""")); break;
            case "deps-target": ChangedFile("NovellMaker.Runtime.deps.json", p => File.WriteAllText(p, File.ReadAllText(p).Replace("win-x64", "linux-x64", StringComparison.Ordinal))); break;
            case "deps-missing-core": ChangedFile("NovellMaker.Runtime.deps.json", p => File.WriteAllText(p, File.ReadAllText(p).Replace("NovellMaker.Core.dll", "other.dll", StringComparison.Ordinal))); break;
            case "deps-nonbinary": ChangedFile("NovellMaker.Runtime.deps.json", p => File.WriteAllText(p, File.ReadAllText(p).Replace("NovellMaker.Core.dll", ".env", StringComparison.Ordinal))); break;
            case "core-wrong-assembly": ChangedFile("NovellMaker.Core.dll", p => File.Copy(Path.Combine(template, "NovellMaker.Runtime.dll"), p, true)); break;
        }
        if (mutation != "missing-manifest") File.WriteAllText(path, manifest.ToJsonString());
        var probe = new ProbeAssets(); var destination = Path.Combine(root, "invalid-game");
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => new WindowsExportService(template, probe)
            .ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.AreEqual(0, probe.Copies); Assert.IsFalse(error.ToString().Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(destination)); Assert.IsEmpty(Directory.GetDirectories(root, ".novell-export-*"));
        if (!OperatingSystem.IsWindows()) return;
        // The installer must reject the same payload before starting its compiler.
        var compiler = Path.Combine(root, "compiler"); Directory.CreateDirectory(compiler);
        File.WriteAllText(Path.Combine(compiler, "makensis.exe"), "must never execute");
        var exe = Path.Combine(root, "invalid.exe");
        await Assert.ThrowsExactlyAsync<IOException>(() => new WindowsInstallerService(new(template, probe), compiler)
            .ExportAsync(project, source, project.Scenes[0].Id, exe, new()));
        Assert.AreEqual(0, probe.Copies); Assert.IsFalse(File.Exists(exe));
        Assert.IsEmpty(Directory.GetDirectories(root, ".novell-installer-*"));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task TemplateMutationAfterAdmissionCannotPublishModifiedBytesOrExtraFiles(bool extra)
    {
        var probe = new ProbeAssets(() => File.WriteAllText(Path.Combine(template, extra ? ".env" : "SDL2.dll"), "PRIVATE-TEMPLATE-CREDENTIAL"));
        var destination = Path.Combine(root, "changed-game");
        await Assert.ThrowsExactlyAsync<IOException>(() => new WindowsExportService(template, probe).ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.AreEqual(1, probe.Copies); Assert.IsFalse(Directory.Exists(destination));
        Assert.IsEmpty(Directory.GetDirectories(root, ".novell-export-*"));
    }

    [TestMethod]
    public void ManifestCreatorRejectsUnexpectedFilesRatherThanBlessingThem()
    {
        File.Delete(Path.Combine(template, RuntimeTemplateManifest.FileName));
        File.WriteAllText(Path.Combine(template, ".env"), "PRIVATE-TEMPLATE-CREDENTIAL");
        Assert.ThrowsExactly<IOException>(() => RuntimeTemplateManifest.Create(template));
        Assert.IsFalse(File.Exists(Path.Combine(template, RuntimeTemplateManifest.FileName)));
    }

    [TestMethod]
    public void LinkedTemplateFileIsRejectedWithoutReadingItsTarget()
    {
        var target = Path.Combine(root, "private.txt"); File.WriteAllText(target, "PRIVATE-LINKED-CREDENTIAL");
        var link = Path.Combine(template, "SDL2.dll"); var bytes = File.ReadAllBytes(link); File.Delete(link);
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { File.WriteAllBytes(link, bytes); Assert.Inconclusive("Symlink creation unavailable."); return; }
        var errorResult = Assert.ThrowsExactly<IOException>(() => RuntimeTemplateManifest.Validate(template));
        Assert.IsFalse(errorResult.ToString().Contains("PRIVATE-LINKED-CREDENTIAL", StringComparison.Ordinal));
        File.Delete(link); Assert.AreEqual("PRIVATE-LINKED-CREDENTIAL", File.ReadAllText(target));
    }

    private sealed class ProbeAssets(Action? mutate = null) : IImageAssetService
    {
        internal int Copies;
        private readonly ImageAssetService inner = new();
        public Task CopyAsync(string source, string target, IReadOnlyList<NovellMaker.Core.Assets.ImageAsset> images, CancellationToken token = default)
        { Copies++; mutate?.Invoke(); return inner.CopyAsync(source, target, images, token); }
        public Task<NovellMaker.Core.Assets.ImageAsset> ImportAsync(string directory, string source, CancellationToken token = default) => inner.ImportAsync(directory, source, token);
        public Task<byte[]> ReadPreviewAsync(string directory, NovellMaker.Core.Assets.ImageAsset image, int width = 1280, CancellationToken token = default) => inner.ReadPreviewAsync(directory, image, width, token);
    }

    private string root = null!;
    private string source = null!;
    private string template = null!;
    private NovelProject project = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "NovellMaker.Export.Tests", Guid.NewGuid().ToString("N"));
        source = Path.Combine(root, "source");
        template = Path.Combine(root, "template"); Directory.CreateDirectory(template);
        project = NovellMaker.Testing.AudioFixture.Attach(BuiltInExample.Create(source), source);
        RuntimeTemplateFixture.Create(template);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var gameRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Games");
        var gameDirectory = Path.GetFullPath(Path.Combine(gameRoot, project.Id.ToString()));
        if (Path.GetDirectoryName(gameDirectory) == gameRoot && Directory.Exists(gameDirectory)) Directory.Delete(gameDirectory, true);
        var full = Path.GetFullPath(root);
        if (full.StartsWith(Path.Combine(Path.GetTempPath(), "NovellMaker.Export.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            Directory.Delete(full, true);
    }

    [TestMethod]
    public async Task ReleaseDeclarationPackagesUnsavedSnapshotWithoutPrivateFilesAndRejectsInvalidConfig()
    {
        var store = new JsonProjectStore(); var path = Path.Combine(source, "project.novell");
        await store.SaveAsync(path, project); var before = await File.ReadAllBytesAsync(path);
        var settings = new NovellMaker.Core.ExternalServices.ReleaseCheckSettings("github_releases_v1", "author", "novel", "2.3.4");
        var edited = project with { ReleaseCheck = settings };
        await File.WriteAllTextAsync(Path.Combine(source, ".env"), "PRIVATE-CREDENTIAL-DO-NOT-EXPORT");
        var output = Path.Combine(root, "release"); var exporter = new WindowsExportService(template, new ImageAssetService());
        await exporter.ExportAsync(edited, source, edited.Scenes[0].Id, output);
        var loaded = await store.LoadAsync(Path.Combine(output, GamePackage.ProjectName));
        Assert.AreEqual(settings, loaded.ReleaseCheck); GamePackage.Load(output).ValidateProject(loaded);
        Assert.IsFalse(File.Exists(Path.Combine(output, ".env"))); CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        var rejected = Path.Combine(root, "rejected");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(edited with { ReleaseCheck = settings with { Owner = "PRIVATE-CREDENTIAL-DO-NOT-EXPORT!" } }, source, edited.Scenes[0].Id, rejected));
        Assert.IsFalse(error.ToString().Contains("PRIVATE-CREDENTIAL", StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(rejected)); Assert.IsFalse(Directory.EnumerateDirectories(root, ".novell-export-*").Any());
    }

    [TestMethod]
    [DataRow("2.3.4", true)] [DataRow("2.3.4.0", true)] [DataRow("2.3.5", false)]
    public void InstallerVersionMustMatchDeclaredGameVersion(string version, bool valid)
    {
        var configured = project with { ReleaseCheck = new("github_releases_v1", "author", "novel", "2.3.4") };
        var options = new WindowsInstallerOptions(version);
        if (valid) options.ValidateProjectVersion(configured);
        else Assert.Throws<ArgumentException>(() => options.ValidateProjectVersion(configured));
        options.ValidateProjectVersion(project);
    }

    [TestMethod]
    public async Task ExportCopiesCurrentSnapshotAndOnlyRegisteredAssetsWithoutChangingSource()
    {
        File.WriteAllText(Path.Combine(source, "private.txt"), "Do not export");
        var original = Path.Combine(source, "project.novell");
        await new JsonProjectStore().SaveAsync(original, project);
        var originalBytes = await File.ReadAllBytesAsync(original);
        var second = NovellMaker.Core.Scenes.NovelScene.Create("Выбранный старт") with { Lines = [NovellMaker.Core.Scenes.DialogueLine.Create("ru", "Начало игры")] };
        var script = new LuaScriptAsset(Guid.NewGuid(), "export.lua", "game.set('exported_script', 'yes')");
        second = second with { ScriptBlocks = [new(null, NovellMaker.Core.Scenes.ScriptPhase.SceneStart, new RunScriptAction(Guid.NewGuid(), script.Id))] };
        var edited = project with { Name = "Несохранённое название", Scenes = project.Scenes.Add(second), Scripts = project.Scripts.Add(script) };
        var destination = Path.Combine(root, "Игра с пробелами");
        await new WindowsExportService(template, new ImageAssetService()).ExportAsync(edited, source, second.Id, destination);
        var package = GamePackage.Load(destination);
        Assert.AreEqual(edited.Name, package.Title);
        Assert.AreEqual(edited.Id, package.ProjectId);
        Assert.AreEqual(second.Id, package.StartSceneId);
        var exported = await new JsonProjectStore().LoadAsync(Path.Combine(destination, GamePackage.ProjectName));
        Assert.AreEqual("yes", new ScenarioPlayer(exported, second.Id, "ru").Settled().State.Variables["exported_script"]);
        Assert.IsTrue(edited.ContentEquals(await new JsonProjectStore().LoadAsync(Path.Combine(destination, GamePackage.ProjectName))));
        foreach (var image in project.Images)
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(source, image.RelativePath)), await File.ReadAllBytesAsync(Path.Combine(destination, image.RelativePath)));
        foreach (var sound in project.Audio.Assets)
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(source, sound.RelativePath)), File.ReadAllBytes(Path.Combine(destination, sound.RelativePath)));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "private.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "native.pdb")));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(original));
        await Assert.ThrowsExactlyAsync<IOException>(() => new WindowsExportService(template, new ImageAssetService()).ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsTrue(edited.ContentEquals(await new JsonProjectStore().LoadAsync(Path.Combine(destination, GamePackage.ProjectName))));
    }

    [TestMethod]
    public async Task MissingOrCorruptImageLeavesNoPartialBuildAndCanBeRetried()
    {
        var image = Path.Combine(source, project.Images[0].RelativePath);
        var bytes = await File.ReadAllBytesAsync(image);
        var exporter = new WindowsExportService(template, new ImageAssetService());
        var destination = Path.Combine(root, "game");
        File.Delete(image);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination)); Assert.HasCount(0, Directory.GetDirectories(root, ".novell-export-*"));
        await File.WriteAllTextAsync(image, "corrupt");
        await Assert.ThrowsAsync<Exception>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination));
        await File.WriteAllBytesAsync(image, bytes);
        await exporter.ExportAsync(project, source, project.Scenes[0].Id, destination);
        Assert.IsTrue(File.Exists(Path.Combine(destination, "NovellMaker.Runtime.exe")));
    }

    [TestMethod]
    public async Task CyclicModuleDependenciesAreRejectedBeforeCreatingStagingOrOutput()
    {
        var edited = NovellMaker.Editor.Examples.ExampleLuaModulesModule.AddTo(project);
        var handler = edited.Scripts.Single(s => s.Id == edited.Scenes.Last().Canvas.HandlerScriptId);
        var child = edited.Scripts.Single(s => s.Id == handler.References["counter"]);
        edited = edited with { Scripts = edited.Scripts.Replace(child, child with { References = child.References.Add("back", handler.Id) }) };
        var destination = Path.Combine(root, "game");
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsExportService(template, new ImageAssetService()).ExportAsync(edited, source, edited.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination)); Assert.IsEmpty(Directory.GetDirectories(root, ".novell-export-*"));
    }

    [TestMethod]
    public async Task InvalidAudioLeavesNoPartialExport()
    {
        File.WriteAllText(Path.Combine(source, project.Audio.Assets[0].RelativePath), "corrupt");
        var destination = Path.Combine(root, "bad-audio");
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsExportService(template, new ImageAssetService()).ExportAsync(project, source, project.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination)); Assert.HasCount(0, Directory.GetDirectories(root, ".novell-export-*"));
    }

    [TestMethod]
    public async Task FrameworkDependentOrMissingTemplateAndInvalidGraphAreRejected()
    {
        var destination = Path.Combine(root, "game");
        var exporter = new WindowsExportService(template, new ImageAssetService());
        var emptyStart = NovellMaker.Core.Scenes.NovelScene.Create("Пустая стартовая сцена");
        var emptyProject = project with { Scenes = project.Scenes.Add(emptyStart) };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => exporter.ExportAsync(emptyProject, source, emptyStart.Id, destination));
        File.WriteAllText(Path.Combine(template, "NovellMaker.Runtime.runtimeconfig.json"), "{\"runtimeOptions\":{\"framework\":{}}}");
        await Assert.ThrowsExactlyAsync<IOException>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
        InitializeTemplateConfig();
        var broken = project with { Scenes = [project.Scenes[0] with { ScriptBlocks = [new(null, NovellMaker.Core.Scenes.ScriptPhase.SceneStart, new JumpAction(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))] }] };
        await Assert.ThrowsAsync<Exception>(() => exporter.ExportAsync(broken, source, broken.Scenes[0].Id, destination));
        Assert.IsFalse(Directory.Exists(destination));
        File.Delete(Path.Combine(template, "coreclr.dll"));
        await Assert.ThrowsExactlyAsync<IOException>(() => exporter.ExportAsync(project, source, project.Scenes[0].Id, destination));
    }

    private void InitializeTemplateConfig() => File.WriteAllText(Path.Combine(template, "NovellMaker.Runtime.runtimeconfig.json"), RuntimeTemplateFixture.Config);

    [TestMethod]
    [TestCategory("NativeSmoke")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExportedGameRunsFromAnotherDirectoryWithNoDotnetAndRestoresQueuedState(bool authorMenu)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1" || !OperatingSystem.IsWindows())
            Assert.Inconclusive("Set NOVELLMAKER_EXPORT_SMOKE=1 after scripts/prepare-windows-export.ps1 on Windows.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var realTemplate = Path.Combine(repo.FullName, "artifacts", "export-templates", "win-x64");
        // A configured v20 project must load in the bundled runtime without an automatic HTTP request.
        project = project with { ReleaseCheck = new("github_releases_v1", "author", "novel", "1.0.0") };
        if (authorMenu)
        {
            var screen = ScreenFixture.Create("Пауза автора");
            var script = new LuaScriptAsset(Guid.NewGuid(), "author-menu.lua", """
                return {on_load=function() ui.set_text(refs.title, 'Меню автора') end,
                  on_event=function(event) if event.node == refs.continue_button then ui.close() end end}
                """);
            screen = screen with { HandlerScriptId = script.Id, Buttons = screen.Buttons.SetItem(0,
                new(screen.Nodes[2].Id, NovellMaker.Core.Screens.ScreenButtonAction.Lua, null)) };
            screen = screen with { Buttons = screen.Buttons.SetItem(1, new(screen.Nodes[3].Id, NovellMaker.Core.Screens.ScreenButtonAction.Open, project.Scenes.Single(s => s.Name.EndsWith("настройки звука")).Id)) };
            screen = screen with { Nodes = screen.Nodes.Add(new(Guid.NewGuid(), screen.Nodes[0].Id, NovellMaker.Core.Screens.ScreenNodeKind.Image,
                new(20, 240, 60, 50)) { ImageId = project.Images[0].Id, Name = "Спрайт из проекта" }) };
            project = project with { Scenes = project.Scenes.Add(screen.ToScene()), InputBindings = project.InputBindings.Where(b => b.Key != "P").Append(new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, screen.Id)).ToImmutableArray(), Scripts = project.Scripts.Add(script) };
        }
        var storyId = project.Scenes[0].Id;
        if (authorMenu)
        {
            var launcher = NovellMaker.Core.Scenes.NovelScene.Create("Главная сцена");
            var button = new NovellMaker.Core.Screens.ScreenNode(Guid.NewGuid(), launcher.Canvas.Nodes[0].Id,
                NovellMaker.Core.Screens.ScreenNodeKind.Button, new(20, 20, 200, 50), "Начать");
            launcher = launcher with { Canvas = launcher.Canvas with
            {
                Nodes = launcher.Canvas.Nodes.Add(button),
                Buttons = [new(button.Id, NovellMaker.Core.Screens.ScreenButtonAction.Scene, storyId)]
            } };
            project = project with { Scenes = project.Scenes.Insert(0, launcher) };
        }
        var destination = Path.Combine(root, "exported game");
        await new WindowsExportService(realTemplate, new ImageAssetService()).ExportAsync(project, source, project.Scenes[0].Id, destination);
        var moved = Path.Combine(root, "перенесённая игра"); Directory.Move(destination, moved);
        // Removing authoring assets proves the exported game cannot fall back to the project directory.
        foreach (var image in project.Images) File.Delete(Path.Combine(source, image.RelativePath));
        foreach (var sound in project.Audio.Assets) File.Delete(Path.Combine(source, sound.RelativePath));
        var captures = Path.Combine(repo.FullName, "artifacts", "screenshots", "export"); Directory.CreateDirectory(captures);
        var report = Path.Combine(captures, authorMenu ? "author-menu-report.json" : "report.json");
        var plan = Path.Combine(root, "plan.json");
        await File.WriteAllTextAsync(plan, """
            [{"Kind":"tick","Seconds":2},{"Kind":"quicksave"},{"Kind":"tick","Seconds":3},
             {"Kind":"quickload"},{"Kind":"next"},{"Kind":"click","X":650,"Y":300},
             {"Kind":"quicksave"},{"Kind":"click","X":500,"Y":300},{"Kind":"quickload"},{"Kind":"click","X":500,"Y":300},
             {"Kind":"key","Key":"P"},{"Kind":"tick","Seconds":40},{"Kind":"click","X":450,"Y":400},
             {"Kind":"tick","Seconds":40},{"Kind":"back"},{"Kind":"activate"}]
            """);
        if (authorMenu)
            File.WriteAllText(plan, "[{\"Kind\":\"click\",\"X\":60,\"Y\":40}," + File.ReadAllText(plan).TrimStart()[1..]);
        var start = new ProcessStartInfo(Path.Combine(moved, "NovellMaker.Runtime.exe"))
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["DOTNET_ROOT"] = Path.Combine(root, "no-dotnet");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(root, "no-dotnet");
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var arg in new[] { "--smoke-output", Path.Combine(captures, authorMenu ? "author-menu-game.png" : "game.png"), "--smoke-report", report, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("Export smoke timed out."); }
        Assert.AreEqual(0, process.ExitCode, await stderr + await stdout);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var allTrace = json.RootElement.GetProperty("ActionTrace").EnumerateArray().ToArray();
        if (authorMenu) Assert.AreEqual(storyId, allTrace[0].GetProperty("State").GetProperty("SceneId").GetGuid());
        var trace = allTrace.Skip(authorMenu ? 1 : 0).ToArray();
        Assert.AreEqual(16, trace.Length);
        if (authorMenu) Assert.IsTrue(trace[10].GetProperty("ScreenTexts").EnumerateArray().Any(t => t.GetString() == "Меню автора"));
        Assert.AreEqual(2, json.RootElement.GetProperty("ScreenLuaAppliedBatches").GetInt32());
        Assert.IsGreaterThan(0, json.RootElement.GetProperty("PendingScreenLuaDrawFrames").GetInt32());
        Assert.AreEqual(0, json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());
        Assert.IsTrue(trace.All(s => s.GetProperty("Success").GetBoolean()));
        Assert.AreEqual(trace[1].GetProperty("State").GetRawText(), trace[3].GetProperty("State").GetRawText());
        Assert.AreEqual(trace[6].GetProperty("State").GetRawText(), trace[8].GetProperty("State").GetRawText());
        StringAssert.Contains(trace[9].GetProperty("Text").GetString()!, "Ветка успеха");
        for (var i = 10; i <= 15; i++)
            Assert.AreEqual(trace[9].GetProperty("State").GetRawText(), trace[i].GetProperty("State").GetRawText(), $"Screen input reached the packaged story at {i}.");
        Assert.AreEqual(3, trace[12].GetProperty("ScreenCount").GetInt32());
        Assert.AreEqual(2, trace[14].GetProperty("ScreenCount").GetInt32());
        Assert.AreEqual(1, trace[15].GetProperty("ScreenCount").GetInt32());
        Assert.AreEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.IsTrue(json.RootElement.GetProperty("Packaged").GetBoolean());
        Assert.IsGreaterThan(0L, json.RootElement.GetProperty("AudioSubmittedBytes").GetInt64());
        Assert.AreEqual(5, json.RootElement.GetProperty("AudioOpenedSources").GetInt32(), "Initial sound/voice/effect and two loads must restart only sound.");
        Assert.AreEqual(1, json.RootElement.GetProperty("AudioSources").GetInt32());
        Assert.AreEqual(project.Name, json.RootElement.GetProperty("WindowTitle").GetString());
        var quicksave = json.RootElement.GetProperty("SavePath").GetString()!;
        Assert.IsTrue(File.Exists(quicksave));
        Assert.IsFalse(quicksave.StartsWith(moved, StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(quicksave, Path.Combine("Games", project.Id.ToString(), "quicksave.json"));
        Assert.AreEqual(Path.Combine(moved, "System.Private.CoreLib.dll"), json.RootElement.GetProperty("RuntimeLibraryPath").GetString());
        using (var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(captures, authorMenu ? "author-menu-game.png" : "game.png")))
        {
            Assert.IsNotNull(bitmap);
            Assert.AreEqual(new SkiaSharp.SKColor(183, 146, 100), bitmap.GetPixel(720, 380));
        }
        // Startup failure is recorded outside the installation directory, including a usable stderr diagnosis.
        await File.WriteAllTextAsync(Path.Combine(moved, "game.novell"), "{}");
        using var failed = Process.Start(start)!;
        var errorText = failed.StandardError.ReadToEndAsync(); var outputText = failed.StandardOutput.ReadToEndAsync();
        await failed.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(1, failed.ExitCode);
        StringAssert.Contains(await errorText, "Ошибка runtime"); await outputText;
        var log = Path.Combine(Path.GetDirectoryName(quicksave)!, "startup-error.log");
        Assert.IsTrue(File.Exists(log));
        StringAssert.Contains(await File.ReadAllTextAsync(log), "Exception");
    }
}


