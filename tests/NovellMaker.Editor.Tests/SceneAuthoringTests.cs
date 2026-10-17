using NovellMaker.Core.Projects;
using NovellMaker.Core.Styling;
using NovellMaker.Core.Scenes;
using System.Collections.Immutable;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class SceneAuthoringTests
{
    [TestMethod]
    public async Task AddAndOpenScenes_CreatesIndependentIdsAndClosingTabRetainsEdits()
    {
        var session = await NewSessionAsync();

        session.AddScene();
        var first = session.ActiveScene!;
        first.AddLine();
        first.LineText = "Текст первой сцены";
        session.AddScene();
        var second = session.ActiveScene!;
        session.OpenScene(first.Id);

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(2, session.Project!.Scenes.Length);
        Assert.AreEqual(3, session.Documents.Count, "Project settings and both opened scenes must remain visible as documents.");
        session.CloseDocument(first);
        Assert.IsFalse(session.Documents.Contains(first));
        session.OpenScene(first.Id);
        Assert.AreEqual("Текст первой сцены", session.ActiveScene!.LineText);
    }

    [TestMethod]
    public async Task DialogueLines_PreserveIdsTextKeysTranslationsAndOrderAcrossAuthoringOperations()
    {
        var session = await NewSessionAsync();
        session.AddScene();
        var document = session.ActiveScene!;
        document.AddLine();
        document.LineText = "Привет";
        var originalId = document.SelectedLine!.Id;
        var originalKey = document.SelectedLine.TextKey;
        document.EditingLanguage = "en";
        document.LineText = "Hello";
        document.EditingLanguage = "ru";
        document.DuplicateLine();
        var copy = document.SelectedLine!;

        Assert.AreNotEqual(originalId, copy.Id);
        Assert.AreNotEqual(originalKey, copy.TextKey);
        Assert.AreEqual("Привет", copy.Model.Texts["ru"]);
        Assert.AreEqual("Hello", copy.Model.Texts["en"]);

        document.MoveLine(-1);
        Assert.AreEqual(copy.Id, document.Lines[0].Id);
        document.DeleteLine();
        Assert.AreEqual(originalId, document.Lines.Single().Id);
        Assert.AreEqual(originalKey, document.Lines.Single().TextKey);
    }

    [TestMethod]
    public async Task Navigation_OnlyChangesSelectionAndClampsAtBoundaries()
    {
        var session = await NewSessionAsync();
        session.AddScene();
        var document = session.ActiveScene!;
        document.AddLine();
        document.LineText = "Один";
        document.AddLine();
        document.LineText = "Два";
        var before = session.Project!;

        document.Navigate(1);
        document.Navigate(1);
        Assert.AreEqual(2, document.Lines.IndexOf(document.SelectedLine!) + 1);
        document.Navigate(-1);
        document.Navigate(-1);
        Assert.AreEqual(1, document.Lines.IndexOf(document.SelectedLine!) + 1);
        Assert.IsTrue(session.Project!.ContentEquals(before));
    }

    [TestMethod]
    public async Task UndoRedo_CoalescesTypingInvalidatesRedoAndReturnsToSavedSnapshot()
    {
        using var directory = TemporaryDirectory.Create();
        var dialogs = new Dialogs { SavePath = Path.Combine(directory.Path, "history.novell") };
        var session = await NewSessionAsync(dialogs: dialogs);
        session.AddScene();
        var document = session.ActiveScene!;
        document.AddLine();
        document.LineText = "Первый";
        await session.SaveAsync();
        document.LineText = "Второй";
        document.LineText = "Третий";

        session.Undo();
        Assert.AreEqual("Первый", document.LineText, "Typing changes in the same field form one undo group.");
        Assert.IsFalse(session.IsDirty);
        session.Redo();
        Assert.AreEqual("Третий", document.LineText);
        session.Undo();
        document.LineText = "Новая ветка";
        Assert.IsFalse(session.CanRedo);
    }

    [TestMethod]
    public async Task UndoRedo_RestoresProjectNameDraftContent()
    {
        var session = await NewSessionAsync();
        session.ProjectName = "Проект автора";
        session.EndEditGroup();
        session.ProjectName = "Переименованный проект";

        session.Undo();
        Assert.AreEqual("Проект автора", session.ProjectName);
        Assert.IsTrue(session.IsDirty);
        session.Redo();
        Assert.AreEqual("Переименованный проект", session.ProjectName);
    }

    [TestMethod]
    public async Task CreateDeleteScene_UndoRestoresSameIdAndActiveSelection()
    {
        var session = await NewSessionAsync();
        session.AddScene();
        var firstId = session.ActiveScene!.Id;
        session.AddScene();
        var deletedId = session.ActiveScene!.Id;

        session.DeleteActiveScene();
        Assert.IsFalse(session.Project!.Scenes.Any(scene => scene.Id == deletedId));
        session.Undo();

        Assert.IsTrue(session.Project!.Scenes.Any(scene => scene.Id == deletedId));
        Assert.AreEqual(deletedId, session.ActiveScene!.Id);
        Assert.AreNotEqual(firstId, deletedId);
        session.Redo();
        Assert.IsFalse(session.Project.Scenes.Any(scene => scene.Id == deletedId));
    }

    [TestMethod]
    public async Task UndoRedo_AfterClosingEditedSceneTabRestoresContentWithoutDuplicateDocuments()
    {
        var session = await NewSessionAsync();
        session.AddScene();
        var document = session.ActiveScene!;
        document.AddLine();
        document.LineText = "Текст перед закрытием";
        var sceneId = document.Id;
        session.CloseDocument(document);
        Assert.AreEqual(1, session.Documents.Count);

        session.Undo();
        Assert.AreEqual(2, session.Documents.Count);
        Assert.AreEqual(sceneId, session.ActiveScene!.Id);
        Assert.AreEqual("", session.ActiveScene.LineText);
        session.Redo();

        Assert.AreEqual(2, session.Documents.Count);
        Assert.AreEqual(1, session.Documents.OfType<SceneDocumentViewModel>().Count());
        session.OpenScene(sceneId);
        Assert.AreEqual("Текст перед закрытием", session.ActiveScene!.LineText);
    }

    [TestMethod]
    public async Task SaveAndReopen_PreservesScenesLocalizedLinesStylingAndVisibilityOptions()
    {
        using var directory = TemporaryDirectory.Create("сцены");
        var path = Path.Combine(directory.Path, "новелла.novell");
        var dialogs = new Dialogs { SavePath = path, OpenPath = path };
        var session = await NewSessionAsync(dialogs: dialogs);
        session.DialogueCss = ".dialogue { color: #112233; }";
        session.ShowVoiceProgress = false;
        session.ShowVoiceControls = false;
        session.ShowNavigationControls = false;
        session.AddScene();
        var document = session.ActiveScene!;
        document.Name = "Вступление";
        document.UseSceneStyle = true;
        document.Css = ".dialogue { color: #445566; }";
        document.AddLine();
        document.LineText = "Привет";
        document.EditingLanguage = "en";
        document.LineText = "Hello";
        var sceneId = document.Id;
        var lineId = document.SelectedLine!.Id;
        await session.SaveAsync();

        var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git());
        await reopened.OpenProjectAsync();

        var scene = reopened.Project!.Scenes.Single();
        Assert.AreEqual(sceneId, scene.Id);
        Assert.AreEqual(lineId, scene.Lines.Single().Id);
        Assert.AreEqual("Привет", scene.Lines.Single().Texts["ru"]);
        Assert.AreEqual("Hello", scene.Lines.Single().Texts["en"]);
        Assert.AreEqual(".dialogue { color: #112233; }", reopened.DialogueCss);
        Assert.IsFalse(reopened.ShowVoiceProgress);
        Assert.IsFalse(reopened.ShowVoiceControls);
        Assert.IsFalse(reopened.ShowNavigationControls);
        reopened.OpenScene(sceneId);
        Assert.AreEqual(".dialogue { color: #445566; }", reopened.ActiveScene!.Css);
    }

    [TestMethod]
    public async Task InvalidCss_IsRetainedForEditingWithDiagnosticAndSafeFallback()
    {
        var session = await NewSessionAsync();
        session.DialogueCss = ".dialogue { unsupported: 1; }";
        session.AddScene();
        var document = session.ActiveScene!;
        document.UseSceneStyle = true;
        document.Css = ".dialogue { color: #XYZ; }";

        Assert.AreEqual(".dialogue { unsupported: 1; }", session.DialogueCss);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.ProjectCssError));
        Assert.AreEqual(".dialogue { color: #XYZ; }", document.Css);
        Assert.IsFalse(string.IsNullOrWhiteSpace(document.CssError));
        Assert.IsNotNull(document.Style);
        Assert.AreEqual(DialogueCssParser.Parse(DialogueCssParser.DefaultCss).BackgroundColor, document.Style.BackgroundColor);
    }

    [TestMethod]
    public async Task CssErrorsKeepLastValidSample_AndNewProjectStartsWithItsOwnFallback()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "invalid-style.novell");
        var dialogs = new Dialogs { OpenPath = path };
        var session = await NewSessionAsync(dialogs);
        session.AddScene();
        session.DialogueCss = ".dialogue { color: #123456; }";
        session.DialogueCss = ".dialogue { color: broken; }";
        Assert.AreEqual("#123456", session.ActiveScene!.Style!.TextColor);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.ActiveScene.CssError));

        var another = NovelProject.Create("Другой проект") with
        {
            DialogueUi = new() { Css = ".dialogue { color: broken; }" }
        };
        await new JsonProjectStore().SaveAsync(path, another);
        await session.OpenProjectAsync();
        Assert.AreEqual(DialogueCssParser.Parse(DialogueCssParser.DefaultCss).TextColor, session.ProjectStyle!.TextColor,
            "An invalid sheet must not borrow another project's last successful preview.");
        session.AddScene();
        Assert.AreEqual(DialogueCssParser.Parse(DialogueCssParser.DefaultCss).TextColor, session.ActiveScene!.Style!.TextColor);
    }

    [TestMethod]
    public async Task ClearedNullableTextInputsRemainSafeDrafts()
    {
        var session = await NewSessionAsync();
        session.DefaultLanguage = null!;
        session.DialogueCss = null!;

        Assert.AreEqual("", session.DefaultLanguage);
        Assert.AreEqual("", session.DialogueCss);
        Assert.IsFalse(session.SaveCommand.CanExecute(null));
        Assert.IsNotNull(session.ProjectStyle);
        Assert.AreEqual("", session.ProjectCssError);
        session.DefaultLanguage = "ru";
        Assert.IsTrue(session.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task InvalidSceneName_IsSafeDraftStateThatDisablesSaving()
    {
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "invalid-scene.novell") };
        var session = await NewSessionAsync(dialogs);
        session.AddScene();
        session.ActiveScene!.Name = "   ";
        await session.SaveAsync();

        Assert.IsFalse(session.AreDocumentsValid);
        Assert.IsFalse(session.SaveCommand.CanExecute(null));
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task LoadedV2_SavesWithoutFalseConflictButRefusesExternalNestedLineChange()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "внешняя-реплика.novell");
        var dialogs = new Dialogs { SavePath = path, OpenPath = path };
        var author = await NewSessionAsync(dialogs);
        author.AddScene();
        author.ActiveScene!.AddLine();
        author.ActiveScene.LineText = "Исходный текст";
        await author.SaveAsync();

        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git());
        await session.OpenProjectAsync();
        await session.SaveAsync();
        Assert.AreEqual(0, dialogs.Errors.Count, "Saving unchanged v2 content must compare nested values, not collection instances.");

        session.OpenScene(session.Project!.Scenes.Single().Id);
        var document = session.ActiveScene!;
        document.LineText = "Моя правка";
        var store = new JsonProjectStore();
        var external = await store.LoadAsync(path);
        var scene = external.Scenes.Single();
        var changedLine = scene.Lines.Single() with { Texts = scene.Lines.Single().Texts.SetItem("ru", "Внешняя правка") };
        await store.SaveAsync(path, external with { Scenes = external.Scenes.SetItem(0, scene with { Lines = scene.Lines.SetItem(0, changedLine) }) });

        await session.SaveAsync();

        Assert.AreEqual("Внешняя правка", (await store.LoadAsync(path)).Scenes.Single().Lines.Single().Texts["ru"]);
        Assert.AreEqual("Моя правка", document.LineText);
        Assert.IsTrue(session.IsDirty);
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task OpenV1_MigratesInMemoryWithoutMutatingFile_AndUnchangedV2CanSave()
    {
        using var directory = TemporaryDirectory.Create();
        var v1Path = Path.Combine(directory.Path, "старый.novell");
        var id = Guid.NewGuid();
        var v1 = $$"""{"formatVersion":1,"id":"{{id}}","name":"Старый проект"}""";
        await File.WriteAllTextAsync(v1Path, v1);
        var dialogs = new Dialogs { OpenPath = v1Path, SavePath = v1Path };
        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git());

        await session.OpenProjectAsync();

        Assert.AreEqual(NovelProject.CurrentFormatVersion, session.Project!.FormatVersion);
        Assert.AreEqual(v1, await File.ReadAllTextAsync(v1Path));
        await session.SaveAsync();
        Assert.AreEqual(NovelProject.CurrentFormatVersion, (await new JsonProjectStore().LoadAsync(v1Path)).FormatVersion);
        Assert.AreEqual(0, dialogs.Errors.Count);

        var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git());
        await reopened.OpenProjectAsync();
        Assert.AreEqual(NovelProject.CurrentFormatVersion, reopened.Project!.FormatVersion);
        Assert.AreEqual(id, reopened.Project.Id);
    }

    [TestMethod]
    public async Task OpenDifferentProject_ResetsHistoryDocumentsAndSceneCache()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "first.novell");
        var secondPath = Path.Combine(directory.Path, "second.novell");
        var dialogs = new Dialogs { SavePath = firstPath };
        var session = await NewSessionAsync(dialogs);
        session.AddScene();
        var firstSceneId = session.ActiveScene!.Id;
        await session.SaveAsync();
        session.ActiveScene.Name = "Несохранённая сцена";
        Assert.IsTrue(session.CanUndo);
        var second = NovelProject.Create("Другой проект");
        await new JsonProjectStore().SaveAsync(secondPath, second);
        dialogs.OpenPath = secondPath;

        await session.OpenProjectAsync();

        Assert.AreEqual(second.Id, session.Project!.Id);
        Assert.AreEqual(0, session.Project.Scenes.Length);
        Assert.IsFalse(session.CanUndo);
        Assert.IsFalse(session.CanRedo);
        Assert.AreEqual(1, session.Documents.Count);
        session.OpenScene(firstSceneId);
        Assert.IsNull(session.ActiveScene);
    }

    [TestMethod]
    public async Task EditingLongScenePreservesSelectionTranslationsAndUndoableOrder()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "long-scene.novell");
        var lines = Enumerable.Range(0, 500).Select(index =>
        {
            var line = DialogueLine.Create("ru", $"Реплика {index}");
            return line with { Texts = line.Texts.SetItem("en", $"Line {index}") };
        }).ToImmutableArray();
        var scene = NovelScene.Create("Длинная сцена") with { Lines = lines };
        await new JsonProjectStore().SaveAsync(path, NovelProject.Create("Проект") with { Scenes = [scene] });
        var session = new EditorSession(new JsonProjectStore(), new Dialogs { OpenPath = path }, new Git());
        await session.OpenProjectAsync();
        session.OpenScene(scene.Id);
        var document = session.ActiveScene!;
        document.SelectLine(lines[250].Id);

        document.LineText = "Изменённая реплика";
        document.MoveLine(-1);
        Assert.AreEqual(lines[250].Id, document.Lines[249].Id);
        session.Undo();
        Assert.AreEqual(lines[250].Id, document.SelectedLine!.Id);
        Assert.AreEqual(lines[250].Id, document.Lines[250].Id);
        Assert.AreEqual("Изменённая реплика", document.LineText);
        session.Undo();
        Assert.AreEqual("Реплика 250", document.LineText);
        Assert.IsFalse(session.IsDirty);
        session.Redo();
        Assert.AreEqual("Изменённая реплика", document.LineText);
        Assert.AreEqual("Line 250", document.SelectedLine.Model.Texts["en"]);
        Assert.AreEqual(500, document.Lines.Count);
        foreach (var original in lines.Where(line => line.Id != lines[250].Id))
            Assert.IsTrue(original.ContentEquals(document.Lines.Single(row => row.Id == original.Id).Model));
    }

    private static async Task<EditorSession> NewSessionAsync(Dialogs? dialogs = null)
    {
        var session = new EditorSession(new JsonProjectStore(), dialogs ?? new Dialogs(), new Git());
        await session.NewProjectAsync();
        return session;
    }

    private sealed class Dialogs : IEditorDialogs
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public List<(string Title, string Message)> Errors { get; } = [];
        public Task<string?> PickOpenProjectAsync() => Task.FromResult(OpenPath);
        public Task<string?> PickSaveProjectAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message) { Errors.Add((title, message)); return Task.CompletedTask; }
    }

    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string projectDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string root, string path) { Root = root; Path = path; }
        public string Root { get; }
        public string Path { get; }
        public static TemporaryDirectory Create(string? child = null)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.SceneTests", Guid.NewGuid().ToString("N"));
            var path = child is null ? root : System.IO.Path.Combine(root, child);
            Directory.CreateDirectory(path);
            return new(root, path);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
