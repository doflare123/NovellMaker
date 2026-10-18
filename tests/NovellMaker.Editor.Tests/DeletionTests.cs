using System.Collections.Immutable;
using SkiaSharp;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class DeletionTests
{
    [TestMethod]
    public async Task SceneDeletionRequiresConfirmationAndTargetsCapturedScene()
    {
        using var f = await Fixture.Create();
        var before = f.Session.Project!;
        await f.Session.DeleteSceneAsync(f.SceneId);
        Assert.IsTrue(before.ContentEquals(f.Session.Project));
        Assert.AreEqual(1, f.Dialogs.Confirmations.Count);
        f.Dialogs.Accept = true;
        await f.Session.DeleteSceneAsync(f.SceneId);
        Assert.AreEqual(0, f.Session.Project!.Scenes.Length);
        f.Session.Undo(); Assert.IsTrue(before.ContentEquals(f.Session.Project));
        f.Session.Redo(); Assert.AreEqual(0, f.Session.Project.Scenes.Length);
    }

    [TestMethod]
    public async Task CharacterDeletionCleansOnlyItsReferencesAndUndoRestoresThem()
    {
        using var f = await Fixture.Create(); f.Dialogs.Accept = true;
        var before = f.Session.Project!;
        await f.Session.DeleteCharacterAsync(f.CharacterId);
        Assert.AreEqual(1, f.Session.Project!.Characters.Length);
        var line = f.Session.Project.Scenes[0].Lines[0];
        Assert.IsNull(line.SpeakerId); Assert.IsNull(line.CharacterStateId); Assert.IsNull(line.HideCharacterId);
        Assert.AreEqual(f.OtherCharacterId, line.Placements.Single().CharacterId);
        Assert.IsTrue(File.Exists(f.ImagePath));
        f.Session.Undo(); Assert.IsTrue(before.ContentEquals(f.Session.Project));
    }

    [TestMethod]
    public async Task StateDeletionChoosesRemainingDefaultAndClearsLineCommands()
    {
        using var f = await Fixture.Create(); f.Dialogs.Accept = true;
        var before = f.Session.Project!;
        await f.Session.DeleteStateAsync(f.CharacterId, f.StateId);
        var character = f.Session.Project!.Characters.Single(c => c.Id == f.CharacterId);
        Assert.AreEqual(1, character.States.Length);
        Assert.AreEqual(character.States[0].Id, character.DefaultStateId);
        Assert.IsNull(f.Session.Project.Scenes[0].Lines[0].CharacterStateId);
        f.Session.Undo(); Assert.IsTrue(before.ContentEquals(f.Session.Project));
    }

    [TestMethod]
    public async Task ImageDeletionRestoresFileAndReferencesWithUndoAndRedo()
    {
        using var f = await Fixture.Create(); f.Dialogs.Accept = true;
        var before = f.Session.Project!; var bytes = await File.ReadAllBytesAsync(f.ImagePath);
        await f.Session.DeleteImageAsync(f.ImageId);
        Assert.IsFalse(File.Exists(f.ImagePath)); Assert.IsTrue(f.Session.Project!.Images.IsEmpty);
        Assert.IsTrue(f.Session.Project.Characters.All(c => c.States.IsEmpty && c.DefaultStateId is null));
        Assert.IsNull(f.Session.Project.Scenes[0].BackgroundImageId);
        Assert.IsNull(f.Session.Project.Scenes[0].Lines[0].CharacterStateId);
        f.Session.Undo(); Assert.IsTrue(before.ContentEquals(f.Session.Project));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(f.ImagePath));
        f.Session.Redo(); Assert.IsFalse(File.Exists(f.ImagePath));
        await File.WriteAllTextAsync(f.ImagePath, "external replacement");
        Assert.ThrowsExactly<IOException>(() => f.Session.Undo());
        Assert.AreEqual("external replacement", await File.ReadAllTextAsync(f.ImagePath));
        Assert.IsTrue(f.Session.Project.Images.IsEmpty); Assert.IsTrue(f.Session.CanUndo);
        File.Delete(f.ImagePath); f.Session.Undo();
        Assert.IsTrue(before.ContentEquals(f.Session.Project));
    }

    [TestMethod]
    public async Task DirectoryDeletionBacksUpWholeTreeAndCleansRegisteredAssets()
    {
        using var f = await Fixture.Create(); f.Dialogs.Accept = true;
        var assets = Path.Combine(f.Root, "Assets");
        Directory.CreateDirectory(Path.Combine(assets, "empty"));
        await File.WriteAllTextAsync(Path.Combine(assets, "readme.txt"), "original");
        var before = f.Session.Project!;
        await f.Session.DeleteExplorerEntryAsync(new("Assets/", "", [], FilePath: assets, IsDirectory: true));
        Assert.IsFalse(Directory.Exists(assets)); Assert.IsTrue(f.Session.Project!.Images.IsEmpty);
        f.Session.Undo(); Assert.IsTrue(Directory.Exists(Path.Combine(assets, "empty")));
        Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Combine(assets, "readme.txt")));
        Assert.IsTrue(before.ContentEquals(f.Session.Project));
        await File.WriteAllTextAsync(Path.Combine(assets, "readme.txt"), "changed externally");
        Assert.ThrowsExactly<IOException>(() => f.Session.Redo());
        Assert.AreEqual("changed externally", await File.ReadAllTextAsync(Path.Combine(assets, "readme.txt")));
        Assert.IsTrue(before.ContentEquals(f.Session.Project)); Assert.IsTrue(f.Session.CanRedo);
    }

    [TestMethod]
    public async Task RawFileDeletionCanBeCancelledAndUndoneWithoutChangingProjectData()
    {
        using var f = await Fixture.Create();
        var path = Path.Combine(f.Root, "notes.txt"); await File.WriteAllTextAsync(path, "notes");
        var entry = new ExplorerEntry("notes.txt", "", [], FilePath: path);
        await f.Session.DeleteExplorerEntryAsync(entry); Assert.IsTrue(File.Exists(path));
        f.Dialogs.Accept = true; await f.Session.DeleteExplorerEntryAsync(entry);
        Assert.IsFalse(File.Exists(path)); Assert.IsFalse(f.Session.IsDirty);
        f.Session.Undo(); Assert.AreEqual("notes", await File.ReadAllTextAsync(path));
        f.Session.Redo(); Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task ProtectsProjectRootCurrentProjectGitAndOutsidePaths()
    {
        using var f = await Fixture.Create(); f.Dialogs.Accept = true;
        var git = Path.Combine(f.Root, ".git"); Directory.CreateDirectory(git);
        var gitFile = Path.Combine(git, "config"); await File.WriteAllTextAsync(gitFile, "protected");
        foreach (var path in new[] { f.Root, f.ProjectPath, git, gitFile, Path.Combine(f.Root, "..", "outside.txt") })
            await f.Session.DeleteExplorerEntryAsync(new("protected", "", [], FilePath: path));
        Assert.AreEqual(0, f.Dialogs.Confirmations.Count);
        Assert.AreEqual(5, f.Dialogs.Errors.Count);
        Assert.IsTrue(File.Exists(f.ProjectPath)); Assert.IsTrue(File.Exists(gitFile));
        await f.Session.DeleteExplorerEntryAsync(new("Сцены/", "", [], IsDirectory: true));
        Assert.AreEqual(0, f.Dialogs.Confirmations.Count);
    }

    [TestMethod]
    public async Task ConfirmationBlocksRepeatedDeletionAndRejectsChangedProject()
    {
        using var f = await Fixture.Create();
        f.Dialogs.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Session.DeleteSceneAsync(f.SceneId);
        Assert.IsTrue(f.Session.IsBusy);
        await f.Session.DeleteSceneAsync(f.SceneId);
        Assert.AreEqual(1, f.Dialogs.Confirmations.Count);
        f.Session.ProjectName = "Changed during dialog";
        f.Dialogs.Pending.SetResult(true); await first;
        Assert.AreEqual(1, f.Session.Project!.Scenes.Length);
        Assert.AreEqual(1, f.Dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task SaveAsToAnotherDirectoryDropsFileRedoFromTheOriginalProject()
    {
        using var f = await Fixture.Create(); f.Dialogs.Accept = true;
        var file = Path.Combine(f.Root, "notes.txt"); await File.WriteAllTextAsync(file, "original");
        await f.Session.DeleteExplorerEntryAsync(new("notes.txt", "", [], FilePath: file));
        f.Session.Undo(); Assert.IsTrue(f.Session.CanRedo);
        var target = Path.Combine(f.Root, "copy"); Directory.CreateDirectory(target);
        f.Dialogs.SavePath = Path.Combine(target, "copy.novell");
        await f.Session.SaveAsync(saveAs: true);
        Assert.AreEqual(0, f.Dialogs.Errors.Count);
        Assert.AreEqual(f.Dialogs.SavePath, f.Session.ProjectPath);
        Assert.IsFalse(f.Session.CanRedo); Assert.IsFalse(f.Session.CanUndo);
        Assert.IsTrue(File.Exists(file));
    }

    private sealed class Dialogs : IEditorDialogs
    {
        public string OpenPath { get; set; } = "";
        public string? SavePath { get; set; }
        public bool Accept { get; set; }
        public TaskCompletionSource<bool>? Pending { get; set; }
        public List<string> Confirmations { get; } = [];
        public List<string> Errors { get; } = [];
        public Task<bool> ConfirmDeleteAsync(string name, string consequences) { Confirmations.Add(name + "\n" + consequences); return Pending?.Task ?? Task.FromResult(Accept); }
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(OpenPath);
        public Task<string?> PickSaveProjectAsync(string name) => Task.FromResult(SavePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "No Git"));
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "novell-delete-" + Guid.NewGuid().ToString("N"));
        public string ProjectPath => Path.Combine(Root, "project.novell");
        public string ImagePath => Path.Combine(Root, "Assets", "Images", "sprite.png");
        public Guid SceneId { get; } = Guid.NewGuid();
        public Guid ImageId { get; } = Guid.NewGuid();
        public Guid CharacterId { get; } = Guid.NewGuid();
        public Guid OtherCharacterId { get; } = Guid.NewGuid();
        public Guid StateId { get; } = Guid.NewGuid();
        public Dialogs Dialogs { get; } = new();
        public EditorSession Session { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.ImagePath)!);
            using (var bitmap = new SKBitmap(1, 1))
            {
                bitmap.Erase(SKColors.Red);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(f.ImagePath, data.ToArray());
            }
            var otherState = Guid.NewGuid();
            var project = NovelProject.Create("Deletion fixture") with
            {
                Images = [new(f.ImageId, "Sprite", "Assets/Images/sprite.png", 1, 1)],
                Characters = [new(f.CharacterId, "First") { DefaultStateId = f.StateId, States = [new(f.StateId, "default", f.ImageId), new(otherState, "other", f.ImageId)] },
                    new(f.OtherCharacterId, "Second") { States = [] }],
                Scenes = [new(f.SceneId, "Intro") { BackgroundImageId = f.ImageId, Lines = [DialogueLine.Create("ru", "text") with
                    { SpeakerId = f.CharacterId, CharacterStateId = f.StateId, HideCharacterId = f.CharacterId,
                      Placements = [new(f.CharacterId, new(.2, 1, 1, 0)), new(f.OtherCharacterId, new(.8, 1, 1, 1))] }] }]
            };
            var store = new JsonProjectStore(); await store.SaveAsync(f.ProjectPath, project);
            f.Dialogs.OpenPath = f.ProjectPath;
            f.Session = new(store, f.Dialogs, new Git()); await f.Session.OpenProjectAsync();
            return f;
        }
        public void Dispose() { Session?.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
