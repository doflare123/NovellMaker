using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Testing;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class DialogueImportTests
{
    [TestMethod]
    [TestCategory("NativeRuntime")]
    [DataRow(false)] [DataRow(true)]
    public async Task ImportedJsonCharactersLinesAndWavRunInF6AndStandalone(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1") Assert.Inconclusive("Enable native/export smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var evidence = NativeDelivery.Evidence(repo!.FullName, "dialogue-import", packaged ? "export" : "preview");
        using var f = new Fixture(evidence); await f.Session.NewProjectAsync();
        var source = new DialogueImportSource { Characters = [new("lena", "Лена") { Poses = [new("idle", "Обычная", "actor.png")] }, new("anton", "Антон") { Poses = [new("idle", "Обычная", "actor.png")] }],
            Lines = Enumerable.Range(1, 30).Select(i => new ImportLine { Key = "line-" + i, Speaker = i % 2 == 0 ? "anton" : "lena", Text = "Импортированная реплика " + i, Voice = "take.wav", Pose = "idle" }).ToImmutableArray() };
        await f.Session.ApplyDialogueImportAsync(await f.Plan(source)); f.AssertNoErrors(); var doc = f.Session.ActiveScene!;
        doc.MoveLine(1); await f.Session.SaveAsync();
        var runtime = NativeDelivery.Preview(Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "NovellMaker.Runtime.exe"));
        if (packaged)
        {
            var game = Path.Combine(evidence, "game");
            await new Services.Export.WindowsExportService(NativeDelivery.Template(repo.FullName), new Services.Assets.ImageAssetService()).ExportAsync(f.Session.Project!, f.ProjectRoot, doc.Id, game);
            runtime = Path.Combine(game, "NovellMaker.Runtime.exe");
        }
        var planPath = Path.Combine(evidence, "plan.json"); await File.WriteAllTextAsync(planPath, """[{"Kind":"wait_now","Seconds":0.35},{"Kind":"next"},{"Kind":"wait_now","Seconds":0.25}]""");
        var reportPath = Path.Combine(evidence, "runtime.json"); var start = new System.Diagnostics.ProcessStartInfo(runtime)
        { WorkingDirectory = evidence, UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        if (!packaged) foreach (var arg in new[] { "--project", f.Dialogs.Path, "--assets-root", f.ProjectRoot, "--scene", doc.Id.ToString(), "--line", doc.Lines[0].Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        foreach (var arg in new[] { "--smoke-output", Path.Combine(evidence, "runtime.png"), "--smoke-report", reportPath, "--smoke-plan", planPath }) start.ArgumentList.Add(arg);
        NativeDelivery.Isolate(start, evidence, packaged);
        using var process = System.Diagnostics.Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); Assert.Fail("Imported scene runtime timed out."); }
        Assert.AreEqual(0, process.ExitCode, await errors + await output);
        using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)); var root = report.RootElement; Assert.AreEqual(0, root.GetProperty("Diagnostics").GetArrayLength());
        var trace = root.GetProperty("ActionTrace"); Assert.AreEqual(doc.Lines[0].Id, trace[0].GetProperty("State").GetProperty("LineId").GetGuid()); Assert.AreEqual(doc.Lines[1].Id, trace[2].GetProperty("State").GetProperty("LineId").GetGuid());
        Assert.IsTrue(trace[0].GetProperty("Voice").GetProperty("Progress").GetDouble() > 0); Assert.IsTrue(trace[2].GetProperty("Voice").GetProperty("Progress").GetDouble() > 0);
        Assert.IsGreaterThan(0, root.GetProperty("InitialVisibleCharacterCount").GetInt32());
    }
    [TestMethod]
    public async Task ConfigurableTableImportsThirtyLinesAndUpdatesByKeysWithoutLosingOrderTranslationsOrEvents()
    {
        using var f = new Fixture(); var session = f.Session; await session.NewProjectAsync(); session.AddScene(); var sceneId = session.ActiveScene!.Id;
        var columns = new Dictionary<string, string> { ["lineKey"] = "code", ["characterKey"] = "person", ["speaker"] = "name", ["text"] = "phrase", ["language"] = "lang", ["voice"] = "recording" }.ToImmutableDictionary();
        var profile = new DialogueImportProfile("Лист актёра", ";", "ru", columns);
        var csv = "code;person;name;phrase;lang;recording\n" + string.Join('\n', Enumerable.Range(1, 30).Select(i => $"l{i};{(i % 2 == 0 ? "a;Антон" : "l;Лена")};Фраза {i};ru;{(i == 1 ? "take.wav" : "")}")) + "\nl1;l;Лена;First line;en;";
        var source = DialogueImportReader.FromTable(DialogueImportReader.ReadTable(csv, ";"), profile);
        Assert.AreEqual(30, source.Scenes.Single().Lines.Length);
        var plan = await f.Plan(source, sceneId, profile); Assert.IsTrue(plan.CanApply); Assert.AreEqual(30, plan.NewLines); Assert.AreEqual(2, plan.NewCharacters);
        Assert.IsFalse(Directory.Exists(Path.Combine(f.ProjectRoot, "Assets")), "Preview must not copy resources.");
        await session.ApplyDialogueImportAsync(plan); f.AssertNoErrors();
        var doc = session.ActiveScene!; var ids = doc.Lines.Select(l => l.Id).ToArray(); var key = doc.Lines[0].TextKey;
        Assert.AreEqual("First line", doc.Lines[0].Model.Texts["en"]); var voice = session.Project!.Audio.Voices[ids[0]]["ru"];
        var block = new ScriptBlock(ids[0], ScriptPhase.AfterLine, new WaitAction(Guid.NewGuid(), "timer", .1));
        session.UpdateScene(session.Project.Scenes.Single() with { ScriptBlocks = [block] });
        doc.SelectLine(ids[0]); doc.MoveLine(1); var order = doc.Lines.Select(l => l.Id).ToArray();
        var changed = DialogueImportReader.FromTable(DialogueImportReader.ReadTable(csv.Replace("Лена", "Елена").Replace("Фраза 1;", "Исправленная фраза;"), ";"), profile);
        plan = await f.Plan(changed, sceneId, profile);
        Assert.AreEqual(0, plan.NewLines); Assert.AreEqual(30, plan.UpdatedLines); Assert.AreEqual(0, plan.NewCharacters);
        Assert.IsFalse(plan.Resources.Single().NeedsCopy, "Unchanged imported WAV should be reused.");
        await session.ApplyDialogueImportAsync(plan); f.AssertNoErrors();
        CollectionAssert.AreEqual(order, doc.Lines.Select(l => l.Id).ToArray()); Assert.AreEqual(2, session.Project!.Characters.Length);
        var line = doc.Lines.Single(l => l.Id == ids[0]); Assert.AreEqual(key, line.TextKey); Assert.AreEqual("Исправленная фраза", line.Text); Assert.AreEqual("First line", line.Model.Texts["en"]);
        Assert.AreEqual(block, session.Project.Scenes.Single().ScriptBlocks.Single()); Assert.AreEqual(voice, session.Project.Audio.Voices[ids[0]]["ru"]);
        Assert.AreEqual(1, session.Project.Audio.Assets.Length); Assert.IsTrue(session.Project.Characters.Any(c => c.Name == "Елена"));
        session.Undo(); Assert.AreEqual("Фраза 1", doc.Lines.Single(l => l.Id == ids[0]).Text); session.Redo();
        await session.SaveAsync(); var saved = await new JsonProjectStore().LoadAsync(f.Dialogs.Path);
        Assert.AreEqual(1, saved.Imports.Profiles.Length); Assert.AreEqual("phrase", saved.Imports.Profiles.Single().Columns["text"]);
        f.Dialogs.Path = Path.Combine(f.Root, "copy", "copy.novell"); Directory.CreateDirectory(Path.GetDirectoryName(f.Dialogs.Path)!);
        await session.SaveAsync(true); f.AssertNoErrors(); await session.OpenProjectAsync();
        Assert.IsTrue(saved.ContentEquals(session.Project));
        var repeated = await f.Plan(changed, sceneId, profile); Assert.AreEqual(0, repeated.NewLines);
    }

    [TestMethod]
    public async Task JsonImportsScenesPosesTranslationsAndOptionalVoicesAsOneUndoableOperation()
    {
        using var f = new Fixture(); await f.Session.NewProjectAsync();
        var source = DialogueImportReader.ReadJson("""
            {"version":1,"language":"ru","characters":[{"key":"lena","name":"Лена","defaultPose":"idle","poses":[{"key":"idle","name":"Обычная","image":"actor.png"}]}],
             "scenes":[{"key":"arrival","name":"Прибытие","lines":[
                {"key":"hello","speaker":"lena","pose":"idle","texts":{"ru":"Привет","en":"Hello"},"voices":{"ru":"take.wav"}},
                {"key":"narration","text":"В комнате тихо.","unvoiced":["ru"]}]}]}
            """);
        var before = f.Session.Project!; var plan = await f.Plan(source); Assert.IsTrue(plan.CanApply); Assert.AreEqual(1, plan.NewScenes);
        await f.Session.ApplyDialogueImportAsync(plan); f.AssertNoErrors();
        var p = f.Session.Project!; p.Validate(); var scene = p.Scenes.Single(); var character = p.Characters.Single(); var pose = character.States.Single();
        Assert.AreEqual(pose.Id, character.DefaultStateId); Assert.AreEqual(pose.Id, scene.Lines[0].CharacterStateId); Assert.IsTrue(p.Audio.VoiceOmissions[scene.Lines[1].Id].Contains("ru"));
        Assert.AreEqual(1, p.Images.Length); Assert.AreEqual(1, p.Audio.Assets.Length); var keys = p.Imports.Bindings.ToArray();
        f.Session.Undo(); Assert.IsTrue(before.ContentEquals(f.Session.Project)); f.Session.Redo(); Assert.IsTrue(p.ContentEquals(f.Session.Project));
        var repeat = await f.Plan(source); Assert.AreEqual(0, repeat.NewScenes); Assert.AreEqual(0, repeat.NewCharacters); Assert.AreEqual(0, repeat.NewLines);
        Assert.IsTrue(repeat.Resources.All(r => !r.NeedsCopy)); await f.Session.ApplyDialogueImportAsync(repeat); f.AssertNoErrors();
        Assert.AreEqual(pose.Id, f.Session.Project!.Characters.Single().States.Single().Id); Assert.AreEqual(1, f.Session.Project.Images.Length); Assert.AreEqual(1, f.Session.Project.Audio.Assets.Length);
        await f.Session.SaveAsync(); var reopened = await new JsonProjectStore().LoadAsync(f.Dialogs.Path);
        CollectionAssert.AreEqual(keys, reopened.Imports.Bindings.ToArray());
    }

    [TestMethod]
    public async Task CharactersOnlyAndTextOnlyModesDoNotRequireGraphicsOrWav()
    {
        using var f = new Fixture(); await f.Session.NewProjectAsync();
        var profile = new DialogueImportProfile("Персонажи", ",", "ru", new Dictionary<string, string> { ["speaker"] = "Имя" }.ToImmutableDictionary());
        var source = DialogueImportReader.FromTable(DialogueImportReader.ReadTable("Имя,Описание\nЛена,А\nАнтон,Б", ","), profile);
        var plan = await f.Plan(source); await f.Session.ApplyDialogueImportAsync(plan); f.AssertNoErrors();
        Assert.AreEqual(0, f.Session.Project!.Scenes.Length); Assert.AreEqual(2, f.Session.Project.Characters.Length);
        source = DialogueImportReader.ReadJson("""{"version":1,"lines":[{"speaker":"Лена","text":"Привет","voice":"missing.wav"}]}""");
        plan = await f.Plan(source, voices: false); Assert.IsTrue(plan.CanApply); Assert.AreEqual(0, plan.Resources.Length);
        await f.Session.ApplyDialogueImportAsync(plan); f.AssertNoErrors(); Assert.AreEqual(2, f.Session.Project.Characters.Length); Assert.AreEqual(0, f.Session.Project.Audio.Voices.Count);
    }

    [TestMethod]
    public async Task PreviewReportsBadResourcesAndApplyRejectsChangedFilesWithoutPartialProjectOrCopies()
    {
        using var f = new Fixture(); await f.Session.NewProjectAsync(); await f.Session.SaveAsync();
        var source = DialogueImportReader.ReadJson("""{"version":1,"characters":[{"key":"l","name":"Лена","poses":[{"key":"idle","name":"Обычная","image":"actor.png"}]}],"lines":[{"speaker":"l","text":"Привет","voice":"take.wav"}]}""");
        var plan = await f.Plan(source); Assert.IsTrue(plan.CanApply);
        await File.WriteAllBytesAsync(Path.Combine(f.SourceRoot, "take.wav"), AudioFixture.Wave(4800));
        var before = f.Session.Project!; await f.Session.ApplyDialogueImportAsync(plan);
        Assert.AreEqual(1, f.Dialogs.Errors.Count); Assert.IsTrue(before.ContentEquals(f.Session.Project));
        Assert.AreEqual(0, Directory.EnumerateFiles(f.ProjectRoot, "*", SearchOption.AllDirectories).Count(p => p.EndsWith(".wav") || p.EndsWith(".png")));
        source = DialogueImportReader.ReadJson("""{"version":1,"lines":[{"text":"Привет","voice":"missing.wav"},{"text":"Ещё","voice":"../outside.wav"}]}""");
        plan = await f.Plan(source); Assert.IsFalse(plan.CanApply); Assert.AreEqual(2, plan.Errors.Length);
        source = DialogueImportReader.ReadJson("""{"version":1,"lines":[{"key":"duplicate","text":"А"},{"key":"duplicate","text":"Б"}]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Plan(source));
        Assert.Throws<System.Text.Json.JsonException>(() => DialogueImportReader.ReadJson("""{"version":1,"version":1} """));
    }

    [TestMethod]
    public async Task ExternalKeysAndProfilesMigrateValidateAndPruneWithDeletedObjects()
    {
        using var f = new Fixture(); await f.Session.NewProjectAsync();
        var source = DialogueImportReader.ReadJson("""{"version":1,"lines":[{"key":"first","text":"А"}]}""");
        await f.Session.ApplyDialogueImportAsync(await f.Plan(source)); f.AssertNoErrors(); var doc = f.Session.ActiveScene!; var id = doc.Lines.Single().Id;
        await f.Session.SaveAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(f.Dialogs.Path))!; json["formatVersion"] = 24; json.AsObject().Remove("imports"); await File.WriteAllTextAsync(f.Dialogs.Path, json.ToJsonString());
        var migrated = await new JsonProjectStore().LoadAsync(f.Dialogs.Path); Assert.AreEqual(25, migrated.FormatVersion); Assert.AreEqual(0, migrated.Imports.Bindings.Length);
        doc.DeleteLine(); Assert.IsFalse(f.Session.Project!.Imports.Bindings.Any(b => b.TargetId == id)); f.Session.Undo(); Assert.IsTrue(f.Session.Project.Imports.Bindings.Any(b => b.TargetId == id));
        var broken = f.Session.Project with { Imports = new() { Bindings = [new("source", ImportedObjectKind.Line, "key", Guid.NewGuid())] } };
        Assert.Throws<ArgumentException>(broken.Validate);
        json = JsonNode.Parse(await File.ReadAllTextAsync(f.Dialogs.Path))!; json["formatVersion"] = 25; json["imports"] = null;
        await File.WriteAllTextAsync(f.Dialogs.Path, json.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => new JsonProjectStore().LoadAsync(f.Dialogs.Path));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        private readonly bool retain;
        internal string SourceRoot => Path.Combine(Root, "source");
        internal string ProjectRoot => Path.GetDirectoryName(Dialogs.Path)!;
        internal readonly Dialogs Dialogs;
        internal readonly EditorSession Session;
        internal Fixture(string? evidence = null)
        {
            Root = evidence ?? Path.Combine(Path.GetTempPath(), "NovellMaker.DialogueImport", Guid.NewGuid().ToString("N")); retain = evidence is not null;
            Directory.CreateDirectory(SourceRoot); Directory.CreateDirectory(Path.Combine(Root, "project"));
            File.WriteAllBytes(Path.Combine(SourceRoot, "take.wav"), AudioFixture.Wave());
            using var bitmap = new SKBitmap(8, 16); bitmap.Erase(SKColors.Gray); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using (var file = File.Create(Path.Combine(SourceRoot, "actor.png"))) data.SaveTo(file);
            Dialogs = new() { Path = Path.Combine(Root, "project", "scene.novell") }; Session = new(new JsonProjectStore(), Dialogs, new Git());
        }
        internal Task<DialogueImportPlan> Plan(DialogueImportSource source, Guid? scene = null, DialogueImportProfile? profile = null, bool voices = true) =>
            DialogueImportPlanner.PrepareAsync(Session.Project!, source, new("book", SourceRoot, "ru", scene, null, true, voices, true,
                DialogueImportPlanner.Characters(source).ToImmutableDictionary(c => c.Key, c => DialogueImportPlanner.SuggestTarget(Session.Project!, "book", c)!), profile, Session.ProjectDirectory));
        internal void AssertNoErrors() => Assert.AreEqual(0, Dialogs.Errors.Count, string.Join('\n', Dialogs.Errors));
        public void Dispose() { Session.Dispose(); if (!retain) Directory.Delete(Root, true); }
    }
    private sealed class Dialogs : IEditorDialogs
    {
        internal string Path = ""; internal List<string> Errors = [];
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(Path);
        public Task<string?> PickSaveProjectAsync(string suggestedName) => Task.FromResult<string?>(Path);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string projectDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }
}
