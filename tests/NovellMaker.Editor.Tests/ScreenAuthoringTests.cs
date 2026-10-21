using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.Services.Preview;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class ScreenAuthoringTests
{
    [TestMethod]
    public async Task ComponentNodeLimitAndLegacyRootExpansionAreAlsoAtomic()
    {
        using var session = await Session(); session.AddScene(); var screen = session.SelectedScreen!;
        session.UpdateScreen(screen with { Nodes = [screen.Nodes[0], .. Enumerable.Range(0, 1023).Select(_ => new ScreenNode(Guid.NewGuid(), screen.Nodes[0].Id, ScreenNodeKind.Text, new(0, 0, 1, 1)))] });
        var before = session.Project; var selection = session.SelectedScreenNodeId;
        session.AddScreenNode(ScreenNodeKind.Text); Assert.AreSame(before, session.Project); Assert.AreEqual(selection, session.SelectedScreenNodeId);
        StringAssert.Contains(session.ScreenAuthoringError, "не добавлен");
        var legacy = ScreenFixture.Create("Старый корень") with { Id = screen.Id, Nodes = [screen.Nodes[0] with { Kind = ScreenNodeKind.Panel, Bounds = new(360, 200, 560, 300) }], References = ImmutableDictionary<string, Guid>.Empty, Buttons = [] };
        var parent = legacy.Nodes[0].Id;
        for (var i = 0; i < 31; i++) { var child = new ScreenNode(Guid.NewGuid(), parent, ScreenNodeKind.Container, new(0, 0, 100, 100)); legacy = legacy with { Nodes = legacy.Nodes.Add(child) }; parent = child.Id; }
        session.UpdateScreen(legacy); before = session.Project;
        session.AddScreenNodeAt(ScreenNodeKind.Text, 20, 20); Assert.AreSame(before, session.Project); StringAssert.Contains(session.ScreenAuthoringError, "не добавлен");
    }
    [TestMethod]
    public async Task RepeatedContainerDropAtOnePointRejectsDepthLimitWithoutChangingProjectOrSelection()
    {
        using var session = await Session(); session.AddScene();
        for (var i = 0; i < 31; i++) session.AddScreenNodeAt(ScreenNodeKind.Container, 50, 50);
        var before = session.Project; var selected = session.SelectedScreenNodeId;
        session.AddScreenNodeAt(ScreenNodeKind.Container, 50, 50);
        Assert.AreSame(before, session.Project); Assert.AreEqual(selected, session.SelectedScreenNodeId); Assert.IsTrue(session.IsDirty);
        StringAssert.Contains(session.ScreenAuthoringError, "Компонент не добавлен");
        session.Undo(); Assert.HasCount(31, session.SelectedScreen!.Nodes); session.Redo(); Assert.IsTrue(before!.ContentEquals(session.Project));
        session.AddScreenNodeAt(ScreenNodeKind.Text, 400, 400); Assert.AreEqual("", session.ScreenAuthoringError);
    }
    [TestMethod]
    public async Task CopyUnifiedScenePreservesSoundVoiceAndEffectWithIndependentLineIds()
    {
        using var folder = new Folder(); var path = Path.Combine(folder.Path, "audio.novell");
        var scene = ScreenFixture.Create("Сцена со звуком").ToScene() with { Lines = [NovellMaker.Core.Scenes.DialogueLine.Create("ru", "Фраза")] };
        var audio = new NovellMaker.Core.Audio.AudioAsset(Guid.NewGuid(), "Запись", "Assets/Audio/record.wav");
        var project = NovelProject.Create("Новелла") with { Scenes = [scene], Audio = new()
        {
            Assets = [audio], SceneSounds = ImmutableDictionary<Guid, Guid>.Empty.Add(scene.Id, audio.Id),
            LineEffects = ImmutableDictionary<Guid, Guid>.Empty.Add(scene.Lines[0].Id, audio.Id),
            Voices = ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>>.Empty.Add(scene.Lines[0].Id, ImmutableDictionary<string, Guid>.Empty.Add("ru", audio.Id))
        } };
        await new JsonProjectStore().SaveAsync(path, project);
        var dialogs = new Dialogs { SavePath = path }; using var session = await Session(dialogs); await session.OpenProjectAsync(); session.OpenScene(scene.Id);
        session.DuplicateScreen(); var copy = session.Project!.Scenes.Last(); var copiedAudio = session.Project.Audio;
        Assert.AreEqual(audio.Id, copiedAudio.SceneSounds[copy.Id]); Assert.AreEqual(audio.Id, copiedAudio.LineEffects[copy.Lines[0].Id]);
        Assert.AreEqual(audio.Id, copiedAudio.Voices[copy.Lines[0].Id]["ru"]);
        session.Undo(); Assert.HasCount(1, session.Project.Audio.SceneSounds); session.Redo(); Assert.HasCount(2, session.Project.Audio.SceneSounds);
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task DialogueCanvasDuplicationAndDeletionUseOneSceneDocumentAndHistory()
    {
        using var session = await Session(); session.AddScene(); var originalDocument = session.ActiveScene!;
        Assert.IsTrue(session.IsScreenDesigner); originalDocument.AddLine(); originalDocument.LineText = "Реплика в общей сцене";
        originalDocument.SelectedSection = 0; session.OpenScreenLibrary(originalDocument.Id);
        Assert.AreSame(originalDocument, session.SelectedDocument); Assert.HasCount(2, session.Documents);
        session.AddScreenNodeAt(ScreenNodeKind.Button, 50, 80); var node = session.SelectedScreenNode!;
        var scene = session.Project!.Scenes.Single();
        Assert.AreEqual(scene.Id, session.SelectedScreen!.Id); Assert.AreEqual(node.Id, scene.Canvas.Nodes[1].Id);
        Assert.AreEqual("Реплика в общей сцене", scene.Lines[0].Texts["ru"]);
        session.DuplicateScreen(); var copy = session.Project.Scenes.Last();
        Assert.HasCount(1, copy.Lines); Assert.HasCount(2, copy.Canvas.Nodes);
        Assert.AreNotEqual(scene.Lines[0].Id, copy.Lines[0].Id); Assert.AreNotEqual(node.Id, copy.Canvas.Nodes[1].Id);
        session.Undo(); Assert.AreSame(originalDocument, session.SelectedDocument); Assert.HasCount(1, session.Project.Scenes);
        session.Redo(); Assert.HasCount(2, session.Project.Scenes);
        await session.DeleteSceneAsync(scene.Id); Assert.HasCount(1, session.Project.Scenes);
        session.Undo(); Assert.IsTrue(scene.ContentEquals(session.Project.Scenes.First()));
    }

    [TestMethod]
    public async Task VisualDropUsesParentCoordinatesAndLegacyRootExpansionIsOneUndo()
    {
        using var session = await Session(); session.AddScreen(empty: true);
        session.AddScreenNodeAt(ScreenNodeKind.Panel, 100, 100); var panel = session.SelectedScreenNode!;
        session.AddScreenNodeAt(ScreenNodeKind.Button, 125, 140); var child = session.SelectedScreenNode!;
        Assert.AreEqual(panel.Id, child.ParentId); Assert.AreEqual(25d, child.Bounds.X); Assert.AreEqual(40d, child.Bounds.Y);
        Assert.AreEqual(125d, session.SelectedScreen!.Definition().AbsoluteBounds(child.Id).X);
        session.Undo(); Assert.IsFalse(session.SelectedScreen!.Nodes.Any(n => n.Id == child.Id));
        session.AddTestScreen(); var legacy = session.SelectedScreen!;
        session.AddScreenNodeAt(ScreenNodeKind.Image, 50, 40);
        Assert.HasCount(legacy.Nodes.Length + 2, session.SelectedScreen!.Nodes);
        Assert.AreEqual(new ScreenRect(50, 40, 240, 160), session.SelectedScreen.Definition().AbsoluteBounds(session.SelectedScreenNode!.Id));
        session.Undo(); Assert.IsTrue(legacy.ContentEquals(session.SelectedScreen!));
        var payload = new NovellMaker.Editor.Views.ComponentDrop(ScreenNodeKind.Text).Encode(session.Project!.Id);
        Assert.IsNotNull(NovellMaker.Editor.Views.ComponentDrop.Decode(payload, session));
        Assert.IsNull(NovellMaker.Editor.Views.ComponentDrop.Decode(payload.Replace(session.Project.Id.ToString(), Guid.NewGuid().ToString()), session));
        Assert.IsNull(NovellMaker.Editor.Views.ComponentDrop.Decode($"novellmaker-component:{session.Project.Id}:999:", session));
        Assert.IsNull(NovellMaker.Editor.Views.ComponentDrop.Decode(new NovellMaker.Editor.Views.ComponentDrop(ScreenNodeKind.Image, Guid.NewGuid()).Encode(session.Project.Id), session));
    }

    [TestMethod]
    public async Task MissingAndClosedProjectOperationsDoNotCreateOrMutateResources()
    {
        using var session = new EditorSession(new JsonProjectStore(), new Dialogs(), new Git(), codeEditor: new CodeEditor());
        session.OpenScreenLibrary(); session.AddTestScreen(); session.DuplicateScreen(); session.SetTestShortcut(Guid.NewGuid());
        session.AddScreenNode(ScreenNodeKind.Text); session.MoveScreenNode(1); session.SelectScreen(Guid.NewGuid());
        await session.CreateScreenHandlerAsync(); await session.DeleteScreenAsync(Guid.NewGuid()); await session.DeleteScreenNodeAsync(Guid.NewGuid());
        Assert.IsFalse(session.OpenScreenLibraryCommand.CanExecute(null)); Assert.IsNull(session.Project); Assert.IsEmpty(session.Documents);
        await session.NewProjectAsync(); session.AddTestScreen(); var screen = session.SelectedScreen!;
        var before = session.Project;
        session.UpdateScreen(screen with { Id = Guid.NewGuid() }); session.UpdateScreenNode(screen.Nodes[1] with { Id = Guid.NewGuid() });
        session.SetTestShortcut(Guid.NewGuid()); session.SelectScreenNode(screen.Nodes[0].Id); session.MoveScreenNode(-1);
        Assert.AreSame(before, session.Project);
        session.SelectScreenNode(screen.Nodes[1].Id); session.MoveScreenNode(-1); Assert.AreSame(before, session.Project);
        session.SelectScreen(Guid.NewGuid()); session.DuplicateScreen(); session.AddScreenNode(ScreenNodeKind.Text);
        Assert.IsNull(session.SelectedScreen); Assert.AreSame(before, session.Project);
        await session.CloseProjectAsync(); Assert.IsFalse(session.OpenScreenLibraryCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ClosingAndReopeningMenuDocumentPreservesEditsAndHistory()
    {
        using var session = await Session(); session.AddTestScreen(); var document = (SceneDocumentViewModel)session.SelectedDocument!;
        Assert.IsTrue(document.CanClose); document.CloseCommand.Execute(null);
        Assert.IsFalse(session.Documents.Contains(document)); Assert.IsFalse(session.SelectedDocument is SceneDocumentViewModel);
        document.CloseCommand.Execute(null); session.OpenScreenLibrary(); Assert.AreEqual(document.Id, session.ActiveScene!.Id); document = session.ActiveScene;
        session.SelectedDocument = session.Documents.First(); document.CloseCommand.Execute(null);
        Assert.AreSame(session.Documents.First(), session.SelectedDocument);
        session.Undo(); Assert.IsEmpty(session.Project!.Screens); session.Redo(); Assert.HasCount(1, session.Project.Screens);
        session.OpenScreenLibrary(); Assert.IsInstanceOfType<SceneDocumentViewModel>(session.SelectedDocument);
    }

    [TestMethod]
    public async Task AutoOpenFailureLeavesAtomicHandlerBindingAndScreenDocumentAvailable()
    {
        using var temp = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(temp.Path, "story.novell") };
        var editor = new CodeEditor { OpenOnCreation = true, Fail = true };
        using var session = await Session(dialogs, editor); session.AddTestScreen(); var document = session.SelectedDocument;
        await session.CreateScreenHandlerAsync();
        Assert.AreSame(document, session.SelectedDocument); Assert.IsNotNull(session.SelectedScreen!.HandlerScriptId);
        Assert.HasCount(1, session.Project!.Scripts); Assert.HasCount(1, dialogs.Errors);
        session.Undo(); Assert.IsNull(session.SelectedScreen!.HandlerScriptId); Assert.IsEmpty(session.Project.Scripts);
    }

    [TestMethod]
    public async Task CreateEditDuplicateAndHistoryPreserveStableNodeIdsAndOwnAliases()
    {
        using var session = await Session(); session.AddScene(); session.AddTestScreen();
        var screen = session.SelectedScreen!; var root = screen.Nodes[0];
        session.SelectScreenNode(screen.Nodes[1].Id);
        session.UpdateScreenNode(screen.Nodes[1] with { Text = "Моё меню", Bounds = new(22, 33, 300, 48) });
        Assert.AreEqual(screen.Nodes[1].Id, session.SelectedScreenNode!.Id);
        session.Undo(); Assert.AreEqual("Пауза", session.SelectedScreenNode!.Text);
        session.Redo(); Assert.AreEqual("Моё меню", session.SelectedScreenNode!.Text);
        var beforeCopy = session.Project!;
        session.DuplicateScreen(); var copy = session.SelectedScreen!;
        Assert.AreNotEqual(screen.Id, copy.Id); Assert.AreNotEqual(root.Id, copy.Nodes[0].Id);
        Assert.AreEqual(copy.Nodes[1].Id, copy.References["title"]);
        Assert.IsTrue(copy.Nodes.Skip(1).All(n => n.ParentId == copy.Nodes[0].Id));
        Assert.IsTrue(copy.Buttons.All(b => copy.Nodes.Any(n => n.Id == b.NodeId)));
        session.Undo(); Assert.IsTrue(beforeCopy.ContentEquals(session.Project));
        Assert.AreEqual(screen.Id, session.SelectedScreen!.Id);
        session.Redo(); Assert.AreEqual(copy.Id, session.SelectedScreen!.Id);
        Assert.IsInstanceOfType<SceneDocumentViewModel>(session.SelectedDocument);
        session.SetTestShortcut(copy.Id); session.Undo(); Assert.AreEqual(screen.Id, session.Project!.InputBindings.FirstOrDefault(b => b.Key == "P")?.TargetSceneId);
        session.Redo(); Assert.AreEqual(copy.Id, session.Project.InputBindings.FirstOrDefault(b => b.Key == "P")?.TargetSceneId);
        session.SetTestShortcut(null); Assert.IsNull(session.Project.InputBindings.FirstOrDefault(b => b.Key == "P")?.TargetSceneId);
    }

    [TestMethod]
    public async Task DeletingScreenCleansIncomingTargetsAndPauseAndUndoRestoresEntireGraph()
    {
        using var session = await Session(); session.AddTestScreen(); var first = session.SelectedScreen!;
        session.AddTestScreen(); var second = session.SelectedScreen!;
        session.UpdateScreen(second with { Buttons = [new(second.Nodes[2].Id, ScreenButtonAction.Open, first.Id)] });
        var before = session.Project!;
        await session.DeleteScreenAsync(first.Id);
        Assert.IsNull(session.Project!.InputBindings.FirstOrDefault(b => b.Key == "P")?.TargetSceneId); Assert.IsEmpty(session.Project.Screens.Single().Buttons);
        ScreenAsset.ValidateProject(session.Project);
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project));
        session.Redo(); Assert.HasCount(1, session.Project.Screens);
    }

    [TestMethod]
    public async Task DeleteNodePrunesDescendantsBindingsAliasesAndHistoryRestoresSelection()
    {
        using var session = await Session(); session.AddTestScreen();
        session.AddScreenNode(ScreenNodeKind.Button); var button = session.SelectedScreenNode!;
        var screen = session.SelectedScreen!;
        session.UpdateScreen(screen with { References = screen.References.Add("extra", button.Id), Buttons = screen.Buttons.Add(new(button.Id, ScreenButtonAction.Close, null)) });
        var before = session.Project!;
        await session.DeleteScreenNodeAsync(button.Id);
        Assert.IsFalse(session.SelectedScreen!.Nodes.Any(n => n.Id == button.Id)); Assert.IsFalse(session.SelectedScreen.References.ContainsKey("extra"));
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); Assert.AreEqual(button.Id, session.SelectedScreenNode!.Id);
        session.Redo(); Assert.IsFalse(session.SelectedScreen!.Nodes.Any(n => n.Id == button.Id));
        before = session.Project!; await session.DeleteScreenNodeAsync(session.SelectedScreen.Nodes[0].Id);
        Assert.AreSame(before, session.Project, "The root cannot be deleted.");
    }

    [TestMethod]
    public async Task HandlerCreateDeleteCancelAndExternalOpenKeepScreenDocumentAndHistory()
    {
        using var temp = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(temp.Path, "story.novell") }; var editor = new CodeEditor();
        using var session = await Session(dialogs, editor); session.AddTestScreen(); var screenId = session.SelectedScreen!.Id;
        var document = session.SelectedDocument;
        await session.CreateScreenHandlerAsync();
        Assert.AreSame(document, session.SelectedDocument); Assert.HasCount(1, session.Project!.Scripts);
        var handlerId = session.SelectedScreen!.HandlerScriptId!.Value;
        Assert.AreEqual(handlerId, session.Project.Scripts.Single().Id); Assert.IsEmpty(editor.Opened);
        session.Undo(); Assert.IsNull(session.SelectedScreen!.HandlerScriptId); Assert.IsEmpty(session.Project.Scripts);
        session.Redo(); Assert.AreEqual(handlerId, session.SelectedScreen!.HandlerScriptId);
        await session.SaveAsync(); await session.OpenScriptExternallyAsync(handlerId);
        Assert.AreSame(document, session.SelectedDocument); Assert.AreEqual(screenId, session.SelectedScreen.Id);
        Assert.HasCount(1, editor.Opened); StringAssert.EndsWith(editor.Opened[0], handlerId.ToString("N") + ".lua");
        await session.DeleteScriptAsync(handlerId); Assert.IsNull(session.SelectedScreen.HandlerScriptId);
        session.Undo(); Assert.AreEqual(handlerId, session.SelectedScreen!.HandlerScriptId);
        dialogs.CancelName = true; var before = session.Project;
        await session.CreateScreenHandlerAsync(); Assert.AreSame(before, session.Project);
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task SaveAsReadsLatestExternalHandlerAndF6FromScreenDocumentUsesSameSnapshot()
    {
        using var temp = new Folder(); var destination = Path.Combine(temp.Path, "copy"); Directory.CreateDirectory(destination);
        var dialogs = new Dialogs { SavePath = Path.Combine(temp.Path, "story.novell") }; var preview = new Preview();
        using var session = await Session(dialogs, preview: preview); session.AddScene(); session.ActiveScene!.AddLine(); session.AddTestScreen();
        await session.CreateScreenHandlerAsync(); await session.SaveAsync(); Assert.IsFalse(session.IsDirty);
        var handler = session.Project!.Scripts.Single(); var current = "return {on_event=function() ui.close() end}";
        File.WriteAllText(ScriptFiles.Resolve(temp.Path, handler.Id), current);
        dialogs.SavePath = Path.Combine(destination, "copy.novell"); await session.SaveAsync(true);
        Assert.AreEqual(current, session.Project.Scripts.Single().Source);
        Assert.AreEqual(current, File.ReadAllText(ScriptFiles.Resolve(destination, handler.Id)));
        Assert.IsTrue(session.Project.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        Assert.IsTrue(session.PreviewSceneCommand.CanExecute(null)); await session.PreviewSceneAsync();
        Assert.IsNotNull(preview.Request); Assert.AreSame(session.Project, preview.Request.Project);
        Assert.AreEqual(session.ActiveScene!.Id, preview.Request.SceneId); Assert.IsNull(preview.Request.LineId);
        Assert.IsInstanceOfType<SceneDocumentViewModel>(session.SelectedDocument);
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task CancelledDeletionAndInvalidDraftLeaveProjectAndHistoryUntouched()
    {
        var dialogs = new Dialogs { AcceptDelete = false }; using var session = await Session(dialogs); session.AddTestScreen();
        var before = session.Project; var screen = session.SelectedScreen!;
        await session.DeleteScreenAsync(screen.Id); await session.DeleteScreenNodeAsync(screen.Nodes[1].Id);
        Assert.AreSame(before, session.Project);
        Assert.Throws<ArgumentException>(() => session.UpdateScreen(screen with { References = screen.References.Add("foreign", Guid.NewGuid()) }));
        Assert.Throws<ArgumentException>(() => session.UpdateScreen(screen with { Name = "" }));
        Assert.AreSame(before, session.Project); session.Undo(); Assert.IsEmpty(session.Project!.Screens);
    }

    [TestMethod]
    public async Task ReorderAndDeletionOfParentAndSelfTargetCopyAreStable()
    {
        using var session = await Session(); session.AddTestScreen(); var screen = session.SelectedScreen!;
        session.SelectScreenNode(screen.Nodes[2].Id); session.MoveScreenNode(-1);
        Assert.AreEqual(screen.Nodes[2].Id, session.SelectedScreen!.Nodes[1].Id);
        session.Undo(); Assert.IsTrue(screen.ContentEquals(session.SelectedScreen!));
        session.Redo(); session.MoveScreenNode(1); Assert.IsTrue(screen.ContentEquals(session.SelectedScreen!));
        screen = session.SelectedScreen!;
        session.UpdateScreen(screen with { Buttons = [new(screen.Nodes[2].Id, ScreenButtonAction.Replace, screen.Id)] });
        session.DuplicateScreen(); Assert.AreEqual(session.SelectedScreen!.Id, session.SelectedScreen.Buttons.Single().TargetScreenId);
        var panel = new ScreenNode(Guid.NewGuid(), session.SelectedScreen.Nodes[0].Id, ScreenNodeKind.Panel, new(0, 0, 50, 50));
        var child = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Text, new(0, 0, 20, 20));
        session.UpdateScreen(session.SelectedScreen with { Nodes = session.SelectedScreen.Nodes.Add(panel).Add(child) });
        await session.DeleteScreenNodeAsync(panel.Id);
        Assert.IsFalse(session.SelectedScreen!.Nodes.Any(n => n.Id == panel.Id || n.Id == child.Id));
    }

    private static async Task<EditorSession> Session(Dialogs? dialogs = null, CodeEditor? editor = null, Preview? preview = null)
    {
        var session = new EditorSession(new JsonProjectStore(), dialogs ?? new(), new Git(), codeEditor: editor ?? new(), previewService: preview ?? new());
        await session.NewProjectAsync(); return session;
    }
    private sealed class Dialogs : IEditorDialogs
    {
        public string? SavePath { get; set; }
        public bool AcceptDelete { get; init; } = true;
        public bool CancelName { get; set; }
        public List<string> Errors { get; } = [];
        public Task<string?> EditLuaNameAsync(string name) => Task.FromResult(CancelName ? null : name);
        public Task<string?> PickOpenProjectAsync() => Task.FromResult(SavePath);
        public Task<string?> PickSaveProjectAsync(string name) => Task.FromResult(SavePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task<bool> ConfirmDeleteAsync(string name, string consequences) => Task.FromResult(AcceptDelete);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
    }
    private sealed class CodeEditor : IExternalCodeEditor
    {
        public bool OpenOnCreation { get; set; }
        public bool Fail { get; init; }
        public string? Executable => null;
        public List<string> Opened { get; } = [];
        public void Configure(string? executable) { }
        public void Open(string file) { if (Fail) throw new IOException("Приложение недоступно."); Opened.Add(file); }
    }
    private sealed class Preview : IPreviewService
    {
        public PreviewRequest? Request { get; private set; }
        public Task StartAsync(PreviewRequest request, Action<string> report) { Request = request; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.ScreenTests", Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
