using System.Collections.Immutable;
using System.Text;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class ScriptAuthoringTests
{
    [TestMethod]
    public async Task SourceEditsRenameAttachAndDeleteSupportUndoWithoutLosingLibrarySelection()
    {
        using var session = await Session(); session.AddScene(); var sceneId = session.ActiveScene!.Id;
        session.AddScript(); var script = session.SelectedScript!;
        session.UpdateScript(script with { Name = "chapter.lua", Source = "game.set('started', 'yes')" });
        session.AttachScriptToSceneStart(sceneId, script.Id);
        Assert.AreEqual(InspectorTarget.Script, session.InspectorTarget);
        Assert.AreEqual(sceneId, session.ActiveScene!.Id);
        var player = new ScenarioPlayer(session.Project!, sceneId, "ru");
        Assert.AreEqual("yes", player.State.Variables["started"]);
        await session.DeleteScriptAsync(script.Id);
        Assert.IsEmpty(session.Project!.Scripts); Assert.IsEmpty(session.Project.Scenes[0].ScriptBlocks);
        session.Undo();
        Assert.AreEqual(InspectorTarget.Script, session.InspectorTarget);
        Assert.AreEqual(script.Id, session.SelectedScript!.Id);
        Assert.AreEqual(script.Id, ((RunScriptAction)session.Project.Scenes[0].ScriptBlocks[0].Action).ScriptId);
        session.Redo(); Assert.IsEmpty(session.Project.Scripts);
    }

    [TestMethod]
    public async Task CancelledDeletionKeepsSourceCallsAndReferences()
    {
        var dialogs = new Dialogs { AcceptDelete = false };
        using var session = await Session(dialogs); session.AddScene(); session.AddScript();
        session.AttachScriptToSceneStart(session.Project!.Scenes[0].Id, session.SelectedScript!.Id);
        var before = session.Project;
        await session.DeleteScriptAsync(session.SelectedScript.Id);
        Assert.IsTrue(before.ContentEquals(session.Project));
    }

    [TestMethod]
    public async Task ImportReplaceExportAndSaveAsKeepIdentityReferencesAndExactCode()
    {
        using var directory = new TestDirectory();
        var dialogs = new Dialogs { LuaPath = Path.Combine(directory.Path, "source.lua"), SavePath = Path.Combine(directory.Path, "project.novell"),
            LuaSavePath = Path.Combine(directory.Path, "exported.lua") };
        const string source = "-- Кириллица\ngame.set('result', 'yes')\n";
        await File.WriteAllTextAsync(dialogs.LuaPath, source, new UTF8Encoding(true));
        using var session = await Session(dialogs); await session.ImportScriptAsync(); var id = session.SelectedScript!.Id;
        Assert.AreEqual(source, session.SelectedScript.Source);
        var refs = ImmutableDictionary<string, Guid>.Empty.Add("self", id);
        session.UpdateScript(session.SelectedScript with { References = refs });
        await File.WriteAllTextAsync(dialogs.LuaPath, "game.set('result', 'updated')\n");
        await session.ImportScriptAsync(true);
        Assert.AreEqual(id, session.SelectedScript!.Id); Assert.AreEqual(id, session.SelectedScript.References["self"]);
        await session.ExportScriptAsync(); Assert.AreEqual(session.SelectedScript.Source, await File.ReadAllTextAsync(dialogs.LuaSavePath));
        await session.SaveAsync(); dialogs.SavePath = Path.Combine(directory.Path, "copy.novell"); await session.SaveAsync(true);
        Assert.IsTrue(session.Project!.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        Assert.IsEmpty(dialogs.Errors);
        Assert.AreEqual("game.set('result', 'updated')\n", await File.ReadAllTextAsync(dialogs.LuaPath), "Editing the project must not rewrite its import source.");
    }

    [TestMethod]
    public async Task InvalidUtf8AndOversizedImportsLeaveProjectUntouchedAndExportNeverOverwrites()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "script.lua"); var dialogs = new Dialogs { LuaPath = path, LuaSavePath = path };
        using var session = await Session(dialogs); session.AddScript(); var before = session.Project!;
        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0x80]); await session.ImportScriptAsync(true);
        Assert.IsTrue(before.ContentEquals(session.Project)); Assert.HasCount(1, dialogs.Errors);
        await File.WriteAllTextAsync(path, new string('x', LuaActionScript.MaximumSourceLength + 1)); await session.ImportScriptAsync(true);
        Assert.IsTrue(before.ContentEquals(session.Project)); Assert.HasCount(2, dialogs.Errors);
        await File.WriteAllTextAsync(path, "external"); await session.ExportScriptAsync();
        Assert.AreEqual("external", await File.ReadAllTextAsync(path)); Assert.HasCount(3, dialogs.Errors);
        Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task DeletingResourceCleansDirectCallsAndAliasesAndUndoRestoresAll()
    {
        using var session = await Session(); session.AddScript(); var first = session.SelectedScript!;
        session.AddScript(); var second = session.SelectedScript!;
        session.UpdateScript(second with { Source = "game.run(refs.other)", References = ImmutableDictionary<string, Guid>.Empty.Add("other", first.Id) });
        session.AddScene(); session.AttachScriptToSceneStart(session.ActiveScene!.Id, first.Id);
        var before = session.Project!;
        await session.DeleteScriptAsync(first.Id);
        Assert.IsEmpty(session.Project!.Scenes[0].ScriptBlocks); Assert.IsEmpty(session.Project.Scripts.Single().References);
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project));
    }

    [TestMethod]
    public async Task BlankNameIsAnUnsavedDraftThatPreventsSaving()
    {
        using var session = await Session(); session.AddScript(); session.UpdateScript(session.SelectedScript! with { Name = "" });
        Assert.IsFalse(session.AreDocumentsValid); Assert.IsTrue(session.IsDirty);
        session.Undo(); Assert.IsTrue(session.AreDocumentsValid);
    }

    private static async Task<EditorSession> Session(Dialogs? dialogs = null)
    {
        var session = new EditorSession(new JsonProjectStore(), dialogs ?? new(), new Git());
        await session.NewProjectAsync(); return session;
    }
    private sealed class Dialogs : IEditorDialogs
    {
        public string? LuaPath { get; init; }
        public string? LuaSavePath { get; init; }
        public string? SavePath { get; set; }
        public bool AcceptDelete { get; init; } = true;
        public List<string> Errors { get; } = [];
        public Task<string?> PickLuaScriptAsync() => Task.FromResult(LuaPath);
        public Task<string?> PickSaveLuaScriptAsync(string name) => Task.FromResult(LuaSavePath);
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveProjectAsync(string name) => Task.FromResult(SavePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task<bool> ConfirmDeleteAsync(string name, string consequences) => Task.FromResult(AcceptDelete);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.ScriptTests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
