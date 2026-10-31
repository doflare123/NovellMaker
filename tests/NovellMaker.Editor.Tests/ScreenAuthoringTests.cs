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
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)]
    public async Task AuthorExamplesHaveHistoryExternalLuaAndSurviveSaveAsAndPreview(int exampleKind)
    {
        using var folder = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(folder.Path, "settings.novell") }; var preview = new Preview();
        using var session = await Session(dialogs, preview: preview); session.AddScene(); session.ActiveScene!.AddLine();
        var before = session.Project!; var active = session.ActiveScene.Id;
        if (exampleKind == 6) session.AddReleaseCheckExample(); else if (exampleKind == 5) session.AddLuaRandomExample(); else if (exampleKind == 4) session.AddLuaLibraryExample(); else if (exampleKind == 3) session.AddLuaModulesExample(); else if (exampleKind == 2) session.AddComponentStateExample(); else if (exampleKind == 1) session.AddTriggersExample(); else session.AddPlayerSettingsExample();
        var after = session.Project!;
        Assert.AreEqual(before.Scenes.Length + 1, after.Scenes.Length); Assert.AreEqual(before.Scripts.Length + (exampleKind == 3 ? 3 : 1), after.Scripts.Length);
        Assert.IsNull(after.StartScreenSceneId); Assert.AreEqual(active, session.ActiveScene.Id);
        var example = after.Scenes.Last(); Assert.AreEqual(example.Id, after.InputBindings.Single(b => b.Key == (exampleKind == 6 ? "F5" : exampleKind == 5 ? "F2" : exampleKind == 4 ? "F3" : exampleKind == 3 ? "F4" : exampleKind == 2 ? "F7" : exampleKind == 1 ? "F8" : "F9")).TargetSceneId);
        Assert.IsTrue(after.Scripts.Any(s => s.Id == example.Canvas.HandlerScriptId));
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); session.Redo(); Assert.IsTrue(after.ContentEquals(session.Project));
        await session.SaveAsync(); Assert.IsTrue(after.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        var copy = Path.Combine(folder.Path, "copy"); Directory.CreateDirectory(copy); dialogs.SavePath = Path.Combine(copy, "settings.novell");
        await session.SaveAsync(true); await session.PreviewSceneAsync();
        Assert.IsTrue(after.ContentEquals(preview.Request!.Project));
        foreach (var resource in after.Scripts.Skip(before.Scripts.Length))
        {
            var script = Path.Combine(copy, "Scripts", resource.Id.ToString("N") + ".lua");
            Assert.AreEqual(resource.Source, File.ReadAllText(script));
        }
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task MenuExampleIsOneHistoryOperationAndSurvivesSaveAsPreviewAndDuplication()
    {
        using var folder = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(folder.Path, "menus.novell") }; var preview = new Preview();
        using var session = await Session(dialogs, preview: preview); session.AddScene(); session.ActiveScene!.AddLine(); var story = session.ActiveScene.Id;
        var before = session.Project; session.AddGameMenusExample(); var after = session.Project!;
        Assert.AreEqual(before!.Scenes.Length + 2, after.Scenes.Length); Assert.AreEqual(1, after.Scripts.Length);
        Assert.AreEqual(story, after.Scenes.Single(s => s.Id == after.StartScreenSceneId).Canvas.Buttons.Single(b => b.Action == ScreenButtonAction.NewGame).TargetScreenId);
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); session.Redo(); Assert.IsTrue(after.ContentEquals(session.Project));
        await session.SaveAsync(); Assert.IsTrue(after.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        await session.PreviewSceneAsync(); Assert.IsTrue(after.ContentEquals(preview.Request!.Project));
        var destination = Path.Combine(folder.Path, "copy"); Directory.CreateDirectory(destination); dialogs.SavePath = Path.Combine(destination, "copy.novell");
        await session.SaveAsync(true); Assert.IsTrue(after.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "Scripts", after.Scripts[0].Id.ToString("N") + ".lua")));
        var menu = after.Scenes.Single(s => s.Id == after.StartScreenSceneId); var (copy, _) = NovellMaker.Core.Scenes.SceneCopy.Create(menu);
        Assert.AreEqual(ScreenButtonAction.ContinueGame, copy.Canvas.Buttons[1].Action); Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task StartScreenAndSessionCommandsSurviveHistorySaveAsPreviewAndDeletion()
    {
        using var folder = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(folder.Path, "session.novell") }; var preview = new Preview();
        using var session = await Session(dialogs, preview: preview); session.AddScene(); var story = session.ActiveScene!.Id;
        session.ActiveScene.AddLine(); session.AddScene(); var menu = session.ActiveScene!.Id;
        session.AddScreenNodeAt(ScreenNodeKind.Button, 200, 200); var button = session.SelectedScreenNode!;
        session.SetStartScreen(menu); session.Undo(); Assert.IsNull(session.Project!.StartScreenSceneId);
        session.Redo(); Assert.AreEqual(menu, session.Project!.StartScreenSceneId);
        session.UpdateScreen(session.SelectedScreen! with { Buttons = [new(button.Id, ScreenButtonAction.NewGame, story)] });
        session.SetInputBinding(new("F2", ScreenButtonAction.MainMenu, null));
        var before = session.Project; Assert.Throws<ArgumentException>(() => session.SetStartScreen(Guid.NewGuid())); Assert.AreSame(before, session.Project);
        await session.SaveAsync(); Assert.IsTrue(session.Project!.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        await session.PreviewSceneAsync(); Assert.AreEqual(menu, preview.Request!.Project.StartScreenSceneId);
        session.DuplicateScreen(); Assert.AreEqual(menu, session.Project!.StartScreenSceneId, "Copy does not replace the startup designation.");
        await session.DeleteScreenAsync(menu); Assert.IsNull(session.Project!.StartScreenSceneId);
        session.Undo(); Assert.AreEqual(menu, session.Project!.StartScreenSceneId);
        await session.DeleteScreenAsync(story); Assert.IsEmpty(session.Project!.Scenes.Single(s => s.Id == menu).Canvas.Buttons);
        session.Undo(); Assert.AreEqual(story, session.Project!.Scenes.Single(s => s.Id == menu).Canvas.Buttons.Single().TargetScreenId);
        var destination = Path.Combine(folder.Path, "copy"); Directory.CreateDirectory(destination); dialogs.SavePath = Path.Combine(destination, "copy.novell");
        await session.SaveAsync(true); Assert.IsTrue(session.Project!.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath))); Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task DialogueCompositionStyleAndBindingsSurviveHistorySaveAsAndPreviewSnapshot()
    {
        using var folder = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(folder.Path, "dialogue.novell") }; var preview = new Preview();
        using var session = await Session(dialogs, preview: preview); session.AddScene(); session.ActiveScene!.AddLine(); session.ActiveScene.LineText = "Реплика";
        var panel = session.SelectedScreen!.Nodes.Single(n => n.Dialogue == DialogueBinding.CssPanel); session.SelectScreenNode(panel.Id);
        var before = session.Project; var shown = session.ScreenPresentation!.Nodes.Single(n => n.Id == panel.Id);
        session.UpdateScreenNode(panel with { Bounds = shown.Bounds with { X = 120, Y = 100, Width = 600, Height = 240 }, Geometry = new() { Rotation = 8, Opacity = .8 }, ClipChildren = false });
        var moved = session.Project; Assert.AreEqual(DialogueBinding.Panel, session.SelectedScreenNode!.Dialogue);
        session.Undo(); Assert.IsTrue(before!.ContentEquals(session.Project)); session.Redo(); Assert.IsTrue(moved!.ContentEquals(session.Project));
        session.SetDialogueStyleProperty("font-size", "30px"); Assert.AreEqual(30d, session.ActiveScene.Style!.FontSize);
        session.SetDialogueStyleProperty("background-color", "#224466"); session.EndEditGroup();
        var afterStyle = session.Project;
        Assert.Throws<NovellMaker.Core.Styling.DialogueCssException>(() => session.SetDialogueStyleProperty("font-size", "ошибка")); Assert.AreSame(afterStyle, session.Project);
        session.DuplicateScreenNode(); var copied = session.SelectedScreenNode!; Assert.AreEqual(DialogueBinding.Panel, copied.Dialogue);
        Assert.AreEqual(panel.Geometry with { Rotation = 8, Opacity = .8 }, copied.Geometry);
        var child = session.SelectedScreen!.Nodes.First(n => n.ParentId == copied.Id); session.SelectScreenNode(child.Id);
        var root = session.SelectedScreen.Nodes.Single(n => n.ParentId is null); session.ReparentScreenNode(root.Id);
        Assert.AreEqual(root.Id, session.SelectedScreenNode!.ParentId); Assert.AreEqual(child.Dialogue, session.SelectedScreenNode.Dialogue);
        await session.SaveAsync(); var destination = Path.Combine(folder.Path, "copy"); Directory.CreateDirectory(destination); dialogs.SavePath = Path.Combine(destination, "copy.novell");
        await session.SaveAsync(true); Assert.IsTrue(session.Project!.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        await session.PreviewSceneAsync(); Assert.IsNotNull(preview.Request); Assert.IsTrue(session.Project.ContentEquals(preview.Request.Project)); Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task SubtreeCopyParentOrderAndDeleteKeepRefsAndHistoryAcrossSaveAsAndF6()
    {
        using var folder = new Folder(); var dialogs = new Dialogs { SavePath = Path.Combine(folder.Path, "tree.novell") }; var preview = new Preview();
        using var session = await Session(dialogs, preview: preview); session.AddScene();
        session.AddScreenNodeAt(ScreenNodeKind.Panel, 100, 100); var panel = session.SelectedScreenNode!;
        session.UpdateScreenNode(panel with { Geometry = new() { Rotation = 20, ScaleX = .8, ScaleY = 1.2 }, Color = "#80CCFF", ClipChildren = false }); panel = session.SelectedScreenNode!;
        session.AddScreenNodeAt(ScreenNodeKind.Area, 120, 130); var child = session.SelectedScreenNode!;
        session.UpdateScreen(session.SelectedScreen! with { References = session.SelectedScreen.References.Add("hit", child.Id).Add("panel", panel.Id), Buttons = [new(child.Id, ScreenButtonAction.Close, null)] });
        session.SelectScreenNode(panel.Id); var before = session.SelectedScreen!; session.DuplicateScreenNode(); var copy = session.SelectedScreenNode!;
        Assert.AreNotEqual(panel.Id, copy.Id); Assert.AreEqual(panel.Geometry, copy.Geometry);
        var copiedChild = session.SelectedScreen!.Nodes.Single(n => n.ParentId == copy.Id);
        Assert.AreEqual(copiedChild.Id, session.SelectedScreen.References["hit_copy"]); Assert.AreEqual(copy.Id, session.SelectedScreen.References["panel_copy"]);
        Assert.AreEqual(copiedChild.Id, session.SelectedScreen.Buttons.Last().NodeId);
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.SelectedScreen!)); session.Redo();
        session.SelectScreenNode(copiedChild.Id); session.ReparentScreenNode(panel.Id); Assert.AreEqual(panel.Id, session.SelectedScreenNode!.ParentId);
        Assert.AreEqual(copiedChild.Bounds, session.SelectedScreenNode.Bounds); Assert.AreEqual(copiedChild.Id, session.SelectedScreen.References["hit_copy"]);
        var unchanged = session.Project; var dirty = session.IsDirty;
        session.SelectScreenNode(panel.Id); Assert.Throws<ArgumentException>(() => session.ReparentScreenNode(panel.Id)); Assert.AreSame(unchanged, session.Project); Assert.AreEqual(dirty, session.IsDirty);
        session.SelectScreenNode(child.Id); session.UpdateScreenNode(child with { Order = 5 }); session.MoveScreenNode(-1);
        Assert.AreEqual(child.Id, session.SelectedScreen!.Definition().Buttons[0].Id);
        await session.DeleteScreenNodeAsync(panel.Id); Assert.IsFalse(session.SelectedScreen!.References.ContainsKey("hit")); Assert.IsFalse(session.SelectedScreen.References.ContainsKey("hit_copy"));
        session.Undo(); Assert.AreEqual(child.Id, session.SelectedScreen!.References["hit"]); Assert.AreEqual(copiedChild.Id, session.SelectedScreen.References["hit_copy"]);
        await session.SaveAsync(); var directory = Path.Combine(folder.Path, "copy"); Directory.CreateDirectory(directory); dialogs.SavePath = Path.Combine(directory, "copy.novell");
        await session.SaveAsync(true); Assert.IsTrue(session.Project!.ContentEquals(await new JsonProjectStore().LoadAsync(dialogs.SavePath)));
        await session.PreviewSceneAsync(); Assert.IsNotNull(preview.Request); Assert.IsTrue(session.Project.ContentEquals(preview.Request.Project)); Assert.IsEmpty(dialogs.Errors);
    }

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
        Assert.AreEqual(scene.Id, session.SelectedScreen!.Id); Assert.AreEqual(node.Id, scene.Canvas.Nodes.Single(n => n.Kind == ScreenNodeKind.Button).Id);
        Assert.AreEqual("Реплика в общей сцене", scene.Lines[0].Texts["ru"]);
        session.DuplicateScreen(); var copy = session.Project.Scenes.Last();
        Assert.HasCount(1, copy.Lines); Assert.HasCount(5, copy.Canvas.Nodes);
        Assert.AreNotEqual(scene.Lines[0].Id, copy.Lines[0].Id); Assert.AreNotEqual(node.Id, copy.Canvas.Nodes.Single(n => n.Kind == ScreenNodeKind.Button).Id);
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
