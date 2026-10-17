using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class EditorSessionTests
{
    [TestMethod]
    public async Task NewProject_CreatesUnsavedProjectWithIndependentStableIdentifiers()
    {
        var session = CreateSession(dialogs: new Dialogs { Decision = UnsavedChangesDecision.Discard });

        await session.NewProjectAsync();
        var first = session.Project!;
        await session.NewProjectAsync();

        Assert.IsTrue(session.HasProject);
        Assert.IsTrue(session.IsDirty);
        Assert.IsNull(session.ProjectPath);
        Assert.AreNotEqual(first.Id, session.Project!.Id);
        Assert.IsTrue(session.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task SaveAndOpen_RoundTripsCyrillicNamePathAndStableIdentifier()
    {
        using var directory = TemporaryDirectory.Create("проект автора");
        var path = Path.Combine(directory.Path, "глава 01.novell");
        var dialogs = new Dialogs { SavePath = path, OpenPath = path };
        var session = CreateSession(new JsonProjectStore(), dialogs);

        await session.NewProjectAsync();
        session.ProjectName = "Тайна старого маяка";
        var expectedId = session.Project!.Id;
        await session.SaveAsync();

        var reopened = CreateSession(new JsonProjectStore(), dialogs);
        await reopened.OpenProjectAsync();

        Assert.AreEqual(expectedId, reopened.Project!.Id);
        Assert.AreEqual("Тайна старого маяка", reopened.ProjectName);
        Assert.AreEqual(Path.GetFullPath(path), reopened.ProjectPath);
        Assert.IsFalse(reopened.IsDirty);
    }

    [TestMethod]
    public async Task Save_WhenPickerCancelled_LeavesProjectDirtyAndUnsaved()
    {
        var dialogs = new Dialogs { SavePath = null };
        var session = CreateSession(dialogs: dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Черновик";

        await session.SaveAsync();

        Assert.IsTrue(session.IsDirty);
        Assert.IsNull(session.ProjectPath);
        Assert.AreEqual(0, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task Save_WhenStoreFails_PreservesExistingPathAndDirtyState_AndPreventsClose()
    {
        var store = new ControlledStore();
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "session.novell") };
        var session = CreateSession(store, dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Сохранённый проект";
        await session.SaveAsync();
        var savedPath = session.ProjectPath;
        session.ProjectName = "Не сохранённое изменение";
        store.SaveFailure = new IOException("Диск недоступен");
        dialogs.Decision = UnsavedChangesDecision.Save;

        var closed = await session.CloseProjectAsync();

        Assert.IsFalse(closed);
        Assert.AreEqual(savedPath, session.ProjectPath);
        Assert.AreEqual("Не сохранённое изменение", session.ProjectName);
        Assert.IsTrue(session.IsDirty);
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task Open_WhenCandidateIsCorrupt_KeepsCurrentUnsavedProjectWithoutPromptingToDiscard()
    {
        var store = new ControlledStore { LoadFailure = new ProjectFormatException("Повреждённый файл") };
        var dialogs = new Dialogs { OpenPath = "broken.novell", Decision = UnsavedChangesDecision.Discard };
        var session = CreateSession(store, dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Важный черновик";
        var expectedId = session.Project!.Id;

        await session.OpenProjectAsync();

        Assert.AreEqual(expectedId, session.Project!.Id);
        Assert.AreEqual("Важный черновик", session.ProjectName);
        Assert.IsTrue(session.IsDirty);
        Assert.AreEqual(0, dialogs.ConfirmCount, "The current project must only be discarded after the chosen file has loaded successfully.");
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    [DataRow(UnsavedChangesDecision.Cancel, false, true)]
    [DataRow(UnsavedChangesDecision.Discard, true, false)]
    public async Task CloseProject_RespectsCancelAndDiscardChoices(UnsavedChangesDecision decision, bool expectedClosed, bool expectedProject)
    {
        var dialogs = new Dialogs { Decision = decision };
        var session = CreateSession(dialogs: dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Черновик";

        var closed = await session.CloseProjectAsync();

        Assert.AreEqual(expectedClosed, closed);
        Assert.AreEqual(expectedProject, session.HasProject);
        Assert.AreEqual(1, dialogs.ConfirmCount);
    }

    [TestMethod]
    public async Task RequestClose_SaveChoiceWithCancelledPicker_DeniesClosingAndRetainsDraft()
    {
        var dialogs = new Dialogs { Decision = UnsavedChangesDecision.Save, SavePath = null };
        var session = CreateSession(dialogs: dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Черновик";

        var allowed = await session.RequestCloseAsync();

        Assert.IsFalse(allowed);
        Assert.IsTrue(session.HasProject);
        Assert.IsTrue(session.IsDirty);
    }

    [TestMethod]
    public async Task RestoringSavedName_ClearsDirtyMarkerAndEnablesNormalSave()
    {
        var store = new ControlledStore();
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "restore-name.novell") };
        var session = CreateSession(store, dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Начальное имя";
        await session.SaveAsync();
        session.ProjectName = "Временное имя";
        Assert.IsTrue(session.IsDirty);

        session.ProjectName = "Начальное имя";

        Assert.IsFalse(session.IsDirty);
        Assert.IsFalse(session.DocumentTitle.EndsWith("*", StringComparison.Ordinal));
        Assert.IsTrue(session.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task InvalidWhitespaceName_DoesNotPermitSaving()
    {
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "invalid-name.novell") };
        var session = CreateSession(dialogs: dialogs);
        await session.NewProjectAsync();

        session.ProjectName = "   ";
        await session.SaveAsync();

        Assert.IsFalse(session.IsNameValid);
        Assert.IsTrue(session.IsDirty);
        Assert.IsFalse(session.SaveCommand.CanExecute(null));
        Assert.IsFalse(session.SaveAsCommand.CanExecute(null));
        Assert.IsNull(session.ProjectPath);
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task Commands_AreDisabledAndReentrantNewIsIgnoredWhileSaveIsPending()
    {
        var store = new ControlledStore { PauseSave = true };
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "pending.novell") };
        var session = CreateSession(store, dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Исходный";
        var expectedId = session.Project!.Id;

        var save = session.SaveAsync();
        await store.WaitForSaveStartAsync();
        var newProject = session.NewProjectAsync();

        Assert.IsTrue(session.IsBusy);
        Assert.IsFalse(session.NewCommand.CanExecute(null));
        Assert.IsFalse(session.OpenCommand.CanExecute(null));
        Assert.IsFalse(session.CloseProjectCommand.CanExecute(null));
        Assert.IsFalse(session.SaveCommand.CanExecute(null));
        store.ReleaseSave();
        await Task.WhenAll(save, newProject);

        Assert.AreEqual(expectedId, session.Project!.Id);
        Assert.AreEqual("Исходный", session.ProjectName);
    }

    [TestMethod]
    public async Task EditsMadeWhileSaveIsPending_RemainDirtyAfterSnapshotIsSaved()
    {
        var store = new ControlledStore { PauseSave = true };
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "race.novell") };
        var session = CreateSession(store, dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Первая версия";

        var save = session.SaveAsync();
        await store.WaitForSaveStartAsync();
        session.ProjectName = "Изменение во время сохранения";
        store.ReleaseSave();
        await save;

        Assert.AreEqual("Первая версия", store.SavedProjects.Single().Name);
        Assert.AreEqual("Изменение во время сохранения", session.ProjectName);
        Assert.IsTrue(session.IsDirty);
    }

    [TestMethod]
    public async Task GitRefresh_ReflectsChangesInExplorerAndDistinguishesNonRepository()
    {
        var store = new ControlledStore();
        var git = new GitService
        {
            Snapshot = new(GitAvailability.NotRepository, null, [], "Git: каталог не является репозиторием")
        };
        var dialogs = new Dialogs { SavePath = Path.Combine(Path.GetTempPath(), "git-status.novell") };
        var session = CreateSession(store, dialogs, git);
        await session.NewProjectAsync();
        session.ProjectName = "Git проект";
        await session.SaveAsync();

        Assert.AreEqual("Git: каталог не является репозиторием", session.GitSummary);
        Assert.AreEqual(2, session.Explorer.Count);
        Assert.AreEqual("Изменения Git", session.Explorer[1].Label);
        Assert.AreEqual(0, session.Explorer[1].Children.Count);

        git.Snapshot = new(GitAvailability.Available, Path.GetTempPath(),
            [new GitFileChange("сцена.json", " ", "M")], "Git: 1 изменение");
        await session.RefreshGitAsync();

        Assert.AreEqual("Git: 1 изменение", session.GitSummary);
        Assert.AreEqual(" M  сцена.json", session.Explorer[1].Children.Single().Label);
    }

    [TestMethod]
    public async Task Save_WhenFileChangedExternally_RefusesOverwriteAndPreservesDirtyDraft()
    {
        using var directory = TemporaryDirectory.Create("проект с внешним изменением");
        var path = Path.Combine(directory.Path, "проект.novell");
        var dialogs = new Dialogs { SavePath = path };
        var session = CreateSession(new JsonProjectStore(), dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Исходный проект";
        await session.SaveAsync();
        var baselineId = session.Project!.Id;

        await new JsonProjectStore().SaveAsync(path, session.Project with { Name = "Изменено в другом редакторе" });
        session.ProjectName = "Моё несохранённое изменение";
        await session.SaveAsync();

        var onDisk = await new JsonProjectStore().LoadAsync(path);
        Assert.AreEqual("Изменено в другом редакторе", onDisk.Name);
        Assert.AreEqual(baselineId, onDisk.Id);
        Assert.AreEqual("Моё несохранённое изменение", session.ProjectName);
        Assert.IsTrue(session.IsDirty);
        Assert.AreEqual(Path.GetFullPath(path), session.ProjectPath);
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task Save_WhenOpenedFileIsMalformedOrDeleted_RefusesToOverwriteExternalState()
    {
        using var directory = TemporaryDirectory.Create();
        var malformedPath = Path.Combine(directory.Path, "повреждённый.novell");
        var missingPath = Path.Combine(directory.Path, "удалённый.novell");

        foreach (var path in new[] { malformedPath, missingPath })
        {
            var dialogs = new Dialogs { SavePath = path };
            var session = CreateSession(new JsonProjectStore(), dialogs);
            await session.NewProjectAsync();
            session.ProjectName = "Проект";
            await session.SaveAsync();
            session.ProjectName = "Локальная правка";
            if (path == malformedPath) await File.WriteAllTextAsync(path, "не JSON");
            else File.Delete(path);

            await session.SaveAsync();

            Assert.IsTrue(session.IsDirty, $"The local draft must remain dirty for '{Path.GetFileName(path)}'.");
            Assert.AreEqual(Path.GetFullPath(path), session.ProjectPath);
            Assert.AreEqual(1, dialogs.Errors.Count);
        }

        Assert.AreEqual("не JSON", await File.ReadAllTextAsync(malformedPath));
        Assert.IsFalse(File.Exists(missingPath));
    }

    [TestMethod]
    public async Task SaveAs_ToDifferentPathAllowsExplicitOverwriteAfterOriginalWasChangedExternally()
    {
        using var directory = TemporaryDirectory.Create();
        var originalPath = Path.Combine(directory.Path, "исходный.novell");
        var targetPath = Path.Combine(directory.Path, "новая копия.novell");
        var dialogs = new Dialogs { SavePath = originalPath };
        var store = new JsonProjectStore();
        var session = CreateSession(store, dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Исходный";
        await session.SaveAsync();
        var id = session.Project!.Id;
        await store.SaveAsync(originalPath, session.Project with { Name = "Внешняя правка" });
        session.ProjectName = "Моя новая версия";
        dialogs.SavePath = targetPath;

        await session.SaveAsync(saveAs: true);

        var copy = await store.LoadAsync(targetPath);
        Assert.AreEqual(id, copy.Id);
        Assert.AreEqual("Моя новая версия", copy.Name);
        Assert.AreEqual(Path.GetFullPath(targetPath), session.ProjectPath);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual("Внешняя правка", (await store.LoadAsync(originalPath)).Name);
        Assert.AreEqual(0, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task Open_SamePathAfterChoosingSave_ReloadsTheVersionJustSaved()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "текущий.novell");
        var dialogs = new Dialogs { SavePath = path, OpenPath = path };
        var session = CreateSession(new JsonProjectStore(), dialogs);
        await session.NewProjectAsync();
        session.ProjectName = "Первая версия";
        await session.SaveAsync();
        var id = session.Project!.Id;
        session.ProjectName = "Версия перед повторным открытием";
        dialogs.Decision = UnsavedChangesDecision.Save;

        await session.OpenProjectAsync();

        Assert.AreEqual(id, session.Project!.Id);
        Assert.AreEqual("Версия перед повторным открытием", session.ProjectName);
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(1, dialogs.ConfirmCount);
        Assert.AreEqual(0, dialogs.Errors.Count);
    }

    private static EditorSession CreateSession(IProjectStore? store = null, Dialogs? dialogs = null, GitService? git = null) =>
        new(store ?? new ControlledStore(), dialogs ?? new Dialogs(), git ?? new GitService());

    private sealed class Dialogs : IEditorDialogs
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public UnsavedChangesDecision Decision { get; set; } = UnsavedChangesDecision.Cancel;
        public int ConfirmCount { get; private set; }
        public List<(string Title, string Message)> Errors { get; } = [];
        public Task<string?> PickOpenProjectAsync() => Task.FromResult(OpenPath);
        public Task<string?> PickSaveProjectAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName)
        {
            ConfirmCount++;
            return Task.FromResult(Decision);
        }
        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class GitService : IGitStatusService
    {
        public GitStatusSnapshot Snapshot { get; set; } = new(GitAvailability.NotRepository, null, [], "Git: нет репозитория");
        public Task<GitStatusSnapshot> GetStatusAsync(string projectDirectory, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }

    private sealed class ControlledStore : IProjectStore
    {
        private readonly TaskCompletionSource saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, NovelProject> projects = new(StringComparer.OrdinalIgnoreCase);
        public bool PauseSave { get; init; }
        public Exception? SaveFailure { get; set; }
        public Exception? LoadFailure { get; init; }
        public List<NovelProject> SavedProjects { get; } = [];
        public Task<NovelProject> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (LoadFailure is not null) return Task.FromException<NovelProject>(LoadFailure);
            var fullPath = Path.GetFullPath(path);
            return projects.TryGetValue(fullPath, out var project)
                ? Task.FromResult(project)
                : Task.FromException<NovelProject>(new FileNotFoundException("Проект не найден.", fullPath));
        }
        public async Task SaveAsync(string path, NovelProject project, CancellationToken cancellationToken = default)
        {
            saveStarted.TrySetResult();
            if (PauseSave) await releaseSave.Task;
            if (SaveFailure is not null) throw SaveFailure;
            projects[Path.GetFullPath(path)] = project;
            SavedProjects.Add(project);
        }
        public Task WaitForSaveStartAsync() => saveStarted.Task;
        public void ReleaseSave() => releaseSave.TrySetResult();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string root, string path)
        {
            Root = root;
            Path = path;
        }
        public string Root { get; }
        public string Path { get; }
        public static TemporaryDirectory Create(string? child = null)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.EditorTests", Guid.NewGuid().ToString("N"));
            var path = child is null ? root : System.IO.Path.Combine(root, child);
            Directory.CreateDirectory(path);
            return new(root, path);
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
