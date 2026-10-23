using System.Text;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.Services.Preview;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed partial class ExternalScriptTests
{
    [TestMethod]
    public void OpenOnCreationIsOffByDefaultAndPersistsIndependentlyOfApplication()
    {
        using var root = new TestRoot(); var path = Path.Combine(root.Path, "preferences.json");
        var editor = new ExternalCodeEditor(path); Assert.IsFalse(editor.OpenOnCreation);
        editor.OpenOnCreation = true; editor.Configure(null);
        Assert.IsTrue(new ExternalCodeEditor(path).OpenOnCreation);
        editor.OpenOnCreation = false; Assert.IsFalse(new ExternalCodeEditor(path).OpenOnCreation);
    }

    [TestMethod]
    public async Task CreationWithoutAutoOpenDoesNotSaveOrLaunchAndPhraseCallStillExists()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path);
        var code = new CodeEditor { OpenOnCreation = false };
        using var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), codeEditor: code);
        await session.NewProjectAsync(); session.AddScene(); var scene = session.ActiveScene!; scene.AddLine();
        await session.CreateAndOpenScriptAsync();
        Assert.IsNull(session.ProjectPath); Assert.HasCount(1, session.Project!.Scripts); Assert.IsEmpty(code.Opened);
        await session.CreatePhraseScriptAsync(scene.Id, scene.SelectedLine!.Id, NovellMaker.Core.Scenes.ScriptPhase.AfterLine);
        Assert.IsNull(session.ProjectPath); Assert.HasCount(2, session.Project.Scripts); Assert.HasCount(1, session.Project.Scenes[0].ScriptBlocks);
        Assert.IsEmpty(code.Opened);
        await session.OpenScriptExternallyAsync(session.Project.Scripts[1].Id);
        Assert.HasCount(1, code.Opened); Assert.IsNotNull(session.ProjectPath);
        await session.CreatePhraseScriptAsync(scene.Id, scene.SelectedLine.Id, NovellMaker.Core.Scenes.ScriptPhase.BeforeLine);
        Assert.IsTrue(File.Exists(ScriptFiles.Resolve(root.Path, session.Project.Scripts.Last().Id)));
        Assert.HasCount(1, code.Opened);
        session.ExplorerModeIndex = 1;
        using var browser = new ProjectFolderBrowser(session);
        browser.Navigate(browser.Roots[0].Items.Single(e => e.FilePath == Path.Combine(root.Path, "Scripts")));
        session.InspectScript(session.Project.Scripts.Last().Id);
        session.UpdateScript(session.SelectedScript! with { Name = "renamed_without_extension" });
        browser.Search = "renamed_without_extension";
        Assert.HasCount(1, browser.Tiles); Assert.AreEqual("lua", browser.Tiles[0].Entry.IconKind);
    }

    [TestMethod]
    public async Task NamingCreationRenameAndCancelPreserveFileIdentityCallsAndUndo()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path);
        var editor = new CodeEditor();
        using var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), codeEditor: editor);
        await session.NewProjectAsync();
        dialogs.ScriptNames.Enqueue("intro.lua");
        await session.CreateAndOpenScriptAsync();
        var script = session.SelectedScript!; var path = session.SelectedScriptPath;
        Assert.AreEqual("intro.lua", script.Name); Assert.AreEqual(path, editor.Opened.Single());
        session.AddScene(); session.AttachScriptToSceneStart(session.ActiveScene!.Id, script.Id);
        dialogs.ScriptNames.Enqueue("opening.lua"); await session.RenameScriptAsync(script.Id);
        Assert.AreEqual("opening.lua", session.SelectedScript!.Name);
        Assert.AreEqual(script.Id, session.SelectedScript.Id); Assert.AreEqual(path, session.SelectedScriptPath);
        Assert.AreEqual(script.Source, ScriptFiles.Read(path));
        Assert.AreEqual(script.Id, ((RunScriptAction)session.Project!.Scenes[0].ScriptBlocks.Single().Action).ScriptId);
        session.Undo(); Assert.AreEqual("intro.lua", session.SelectedScript!.Name);
        session.Redo(); Assert.AreEqual("opening.lua", session.SelectedScript!.Name);
        var before = session.Project;
        dialogs.ScriptNames.Enqueue(null); await session.CreateAndOpenScriptAsync();
        dialogs.ScriptNames.Enqueue(null); await session.RenameScriptAsync(script.Id);
        Assert.AreSame(before, session.Project); Assert.HasCount(1, editor.Opened);
        var scene = session.ActiveScene!; scene.AddLine(); before = session.Project;
        dialogs.ScriptNames.Enqueue(null);
        await session.CreatePhraseScriptAsync(scene.Id, scene.SelectedLine!.Id, NovellMaker.Core.Scenes.ScriptPhase.DuringLine);
        Assert.AreSame(before, session.Project); Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public void EditorPreferencePersistsAndFileArgumentIsNeverAShellCommand()
    {
        using var root = new TestRoot();
        var settings = Path.Combine(root.Path, "settings.json");
        var executable = Path.Combine(root.Path, "Code Editor.exe"); File.WriteAllText(executable, "fixture");
        var editor = new ExternalCodeEditor(settings); editor.Configure(executable);
        Assert.AreEqual(executable, new ExternalCodeEditor(settings).Executable);
        var file = Path.Combine(root.Path, "код & file.lua");
        var start = ExternalCodeEditor.StartInfo(file, executable);
        Assert.AreEqual(executable, start.FileName); Assert.IsFalse(start.UseShellExecute);
        Assert.AreEqual(file, start.ArgumentList.Single()); Assert.AreEqual("", start.Arguments);
        editor.Configure(null); Assert.IsNull(new ExternalCodeEditor(settings).Executable);
    }

    [TestMethod]
    public async Task OpeningProjectReadsExistingLuaInsteadOfOverwritingItWithSnapshot()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path);
        using var session = await Session(dialogs); var id = session.SelectedScript!.Id;
        await session.SaveAsync();
        var file = session.SelectedScriptPath;
        await File.WriteAllTextAsync(file, "game.set('external', 'yes')");
        using var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git(), codeEditor: new CodeEditor());
        await reopened.OpenProjectAsync(); reopened.OpenScriptLibrary(id);
        Assert.AreEqual("game.set('external', 'yes')", reopened.SelectedScript!.Source);
        Assert.IsTrue(reopened.IsDirty); Assert.AreEqual("Синтаксис корректен", reopened.SelectedScriptDiagnostic);
        Assert.AreEqual(reopened.SelectedScript.Source, await File.ReadAllTextAsync(file));
    }

    [TestMethod]
    public async Task SaveAndSaveAsImmediatelyReadFilesAndRejectDifferentDestinationCode()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path);
        using var session = await Session(dialogs); await session.SaveAsync(); var id = session.SelectedScript!.Id;
        var file = session.SelectedScriptPath; await File.WriteAllTextAsync(file, "game.set('latest', 'yes')");
        await session.SaveAsync();
        Assert.AreEqual("game.set('latest', 'yes')", (await new JsonProjectStore().LoadAsync(dialogs.ProjectPath)).Scripts.Single().Source);
        var copy = Path.Combine(root.Path, "copy"); Directory.CreateDirectory(copy); dialogs.SavePath = Path.Combine(copy, "copy.novell");
        ScriptFiles.Ensure(copy, session.SelectedScript with { Source = "-- foreign code" });
        await session.SaveAsync(true);
        Assert.AreEqual(dialogs.ProjectPath, session.ProjectPath);
        Assert.AreEqual("-- foreign code", ScriptFiles.Read(ScriptFiles.Resolve(copy, id)));
        Assert.IsFalse(File.Exists(dialogs.SavePath)); Assert.HasCount(1, dialogs.Errors);
        var cleanCopy = Path.Combine(root.Path, "clean-copy"); Directory.CreateDirectory(cleanCopy); dialogs.SavePath = Path.Combine(cleanCopy, "copy.novell");
        await session.SaveAsync(true);
        Assert.AreEqual("game.set('latest', 'yes')", ScriptFiles.Read(ScriptFiles.Resolve(cleanCopy, id)));
    }

    [TestMethod]
    public async Task InvalidSavedFileBlocksPreviewAndExportUntilCorrectedWithoutExecutingCode()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); var preview = new Preview();
        using var session = await Session(dialogs, preview); await session.SaveAsync(); var id = session.SelectedScript!.Id;
        session.AttachScriptToSceneStart(session.Project!.Scenes[0].Id, id); session.OpenScene(session.Project.Scenes[0].Id);
        var file = ScriptFiles.Resolve(root.Path, id); await File.WriteAllTextAsync(file, "if then");
        await session.PreviewSceneAsync(); await session.ExportWindowsAsync();
        Assert.AreEqual(0, preview.Starts); Assert.AreEqual(0, dialogs.ExportPickerCalls); Assert.HasCount(2, dialogs.Errors);
        await File.WriteAllTextAsync(file, "while true do end"); await session.RefreshExternalScriptsAsync();
        session.OpenScriptLibrary(id); Assert.AreEqual("Синтаксис корректен", session.SelectedScriptDiagnostic, "Validation must compile only, never run a loop.");
        session.OpenScene(session.Project.Scenes[0].Id); await session.PreviewSceneAsync(); Assert.AreEqual(1, preview.Starts);
    }

    [TestMethod]
    public async Task CyclicModulesCanBeSavedButBlockPreviewAndExportBeforePickingADestination()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); var preview = new Preview();
        using var session = await Session(dialogs, preview); session.AddLuaModulesExample();
        var handler = session.Project!.Scripts.Single(s => s.Id == session.Project.Scenes.Last().Canvas.HandlerScriptId);
        var child = session.Project.Scripts.Single(s => s.Id == handler.References["counter"]);
        session.OpenScriptLibrary(child.Id); session.UpdateScript(child with { References = child.References.Add("back", handler.Id) });
        await session.SaveAsync(); Assert.IsTrue(File.Exists(dialogs.ProjectPath));
        await session.PreviewSceneAsync(); await session.ExportWindowsAsync();
        Assert.AreEqual(0, preview.Starts); Assert.AreEqual(0, dialogs.ExportPickerCalls); Assert.HasCount(2, dialogs.Errors);
        Assert.IsTrue(dialogs.Errors.All(e => e.Contains("Циклическая", StringComparison.Ordinal)));
        session.Undo(); await session.PreviewSceneAsync(); Assert.AreEqual(1, preview.Starts);
    }

    [TestMethod]
    public async Task MissingAndInvalidUtf8FilesKeepSnapshotAndReportErrorsThenRecover()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path);
        using var session = await Session(dialogs); await session.SaveAsync(); var file = session.SelectedScriptPath;
        var original = session.SelectedScript!.Source;
        File.Delete(file); await session.RefreshExternalScriptsAsync();
        Assert.IsFalse(File.Exists(file)); Assert.AreEqual(original, session.SelectedScript.Source);
        Assert.AreNotEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
        await File.WriteAllBytesAsync(file, [0xff, 0xfe, 0x80]); await session.RefreshExternalScriptsAsync();
        Assert.AreEqual(original, session.SelectedScript.Source); Assert.AreNotEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
        await File.WriteAllTextAsync(file, "-- recovered", new UTF8Encoding(true)); await session.RefreshExternalScriptsAsync();
        Assert.AreEqual("-- recovered", session.SelectedScript.Source); Assert.AreEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
    }

    [TestMethod]
    public async Task UndoMetadataDoesNotOverwriteExternallySavedCodeAndOpenUsesConfiguredService()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); var editor = new CodeEditor();
        using var session = await Session(dialogs, editor: editor); await session.SaveAsync();
        var script = session.SelectedScript!; var file = session.SelectedScriptPath;
        session.UpdateScript(script with { Name = "renamed.lua" });
        await File.WriteAllTextAsync(file, "-- latest outside editor"); session.Undo();
        Assert.AreEqual(script.Name, session.SelectedScript!.Name);
        Assert.AreEqual("-- latest outside editor", session.SelectedScript.Source);
        Assert.AreEqual(session.SelectedScript.Source, await File.ReadAllTextAsync(file));
        await session.OpenScriptExternallyAsync(); Assert.AreEqual(file, editor.Opened.Single());
    }

    [TestMethod]
    public async Task PhysicalLuaDeletionUsesFileBackupAndUndoRestoresCodeAndBindings()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path);
        using var session = await Session(dialogs); await session.SaveAsync(); var id = session.SelectedScript!.Id;
        session.AttachScriptToSceneStart(session.Project!.Scenes[0].Id, id);
        var file = session.SelectedScriptPath; await File.WriteAllTextAsync(file, "-- external code before deletion");
        await session.DeleteExplorerEntryAsync(new ExplorerEntry("script.lua", "", [], FilePath: file, ScriptId: id));
        Assert.IsFalse(File.Exists(file)); Assert.IsEmpty(session.Project!.Scripts); Assert.IsEmpty(session.Project.Scenes[0].ScriptBlocks);
        session.Undo(); Assert.IsTrue(File.Exists(file));
        Assert.AreEqual("-- external code before deletion", session.Project.Scripts.Single().Source);
        Assert.IsInstanceOfType<RunScriptAction>(session.Project.Scenes[0].ScriptBlocks.Single().Action);
    }

    [TestMethod]
    public async Task ExternalEditorFailureAfterCreatingPhraseLuaKeepsDocumentAndReportsError()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); var editor = new CodeEditor { Fail = true };
        using var session = await Session(dialogs, editor: editor);
        session.OpenScene(session.Project!.Scenes[0].Id); var scene = session.ActiveScene!;
        scene.AddLine(); scene.LineText = "Не терять этот текст";
        var count = session.Documents.Count;
        await session.CreatePhraseScriptAsync(scene.Id, scene.SelectedLine!.Id, NovellMaker.Core.Scenes.ScriptPhase.DuringLine);
        Assert.AreSame(scene, session.SelectedDocument); Assert.AreEqual(count, session.Documents.Count);
        Assert.AreEqual("Не терять этот текст", scene.LineText); Assert.IsTrue(session.CanEdit);
        Assert.HasCount(1, dialogs.Errors);
        Assert.IsInstanceOfType<RunScriptAction>(session.Project!.Scenes[0].ScriptBlocks.Single().Action);
    }

    private static async Task<EditorSession> Session(Dialogs dialogs, Preview? preview = null, CodeEditor? editor = null)
    {
        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), previewService: preview, codeEditor: editor ?? new CodeEditor());
        await session.NewProjectAsync(); session.AddScene(); session.AddScript(); return session;
    }
    private sealed class Dialogs(string root) : IEditorDialogs
    {
        public Queue<string?> ScriptNames { get; } = new();
        public Task<string?> EditLuaNameAsync(string suggestedName) => Task.FromResult(ScriptNames.TryDequeue(out var name) ? name : suggestedName);
        public string ProjectPath { get; } = Path.Combine(root, "project.novell");
        public string? SavePath { get; set; }
        public List<string> Errors { get; } = [];
        public int ExportPickerCalls { get; private set; }
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(ProjectPath);
        public Task<string?> PickSaveProjectAsync(string name) => Task.FromResult<string?>(SavePath ?? ProjectPath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task<bool> ConfirmDeleteAsync(string name, string consequences) => Task.FromResult(true);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
        public Task<WindowsExportDestination?> PickWindowsExportAsync(NovelProject project) { ExportPickerCalls++; return Task.FromResult<WindowsExportDestination?>(null); }
    }
    private sealed class CodeEditor : IExternalCodeEditor
    {
        public bool OpenOnCreation { get; set; } = true;
        public bool Fail { get; init; }
        public string? Executable { get; private set; }
        public List<string> Opened { get; } = [];
        public void Configure(string? executable) => Executable = executable;
        public void Open(string file) { if (Fail) throw new InvalidOperationException("Нет приложения для Lua"); Opened.Add(file); }
    }
    private sealed class Preview : IPreviewService
    {
        public int Starts { get; private set; }
        public Task StartAsync(PreviewRequest request, Action<string> diagnostic) { Starts++; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }
    private sealed class TestRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.ExternalScripts", Guid.NewGuid().ToString("N"));
        public TestRoot() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
