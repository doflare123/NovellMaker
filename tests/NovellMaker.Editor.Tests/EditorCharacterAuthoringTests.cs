using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class EditorCharacterAuthoringTests
{
    [TestMethod]
    public async Task CharacterWithoutImages_IsValidButStateCreationExplainsImportRequirement()
    {
        var session = await NewSessionAsync(new Dialogs());

        session.AddCharacter();

        Assert.AreEqual(1, session.Project!.Characters.Length);
        Assert.AreEqual(session.Project.Characters.Single().Id, session.SelectedCharacter!.Id);
        Assert.AreEqual(0, session.SelectedCharacter.States.Count);
        Assert.IsFalse(session.CanAddCharacterState);
        Assert.IsFalse(session.AddCharacterStateCommand.CanExecute(null));
        StringAssert.Contains(session.AddCharacterStateHint, "импортируйте");
        session.AddCharacterState();
        Assert.AreEqual(0, session.Project.Characters.Single().States.Length);
    }

    [TestMethod]
    public async Task AddRenameAndReassignState_UsesStableIdsAndUndoRestoresSelection()
    {
        using var directory = TemporaryDirectory.Create();
        var firstSource = CreatePng(directory.Path, "neutral.png", SKColors.SteelBlue);
        var secondSource = CreatePng(directory.Path, "sad.png", SKColors.MediumPurple);
        var dialogs = new Dialogs
        {
            SavePath = Path.Combine(directory.Path, "story.novell"),
            ImagePath = firstSource
        };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        var firstImageId = session.SelectedImage!.Id;
        dialogs.ImagePath = secondSource;
        await session.ImportImageAsync();
        var secondImage = session.SelectedImage!;

        session.AddCharacter();
        session.AddCharacterState();
        var characterId = session.SelectedCharacter!.Id;
        var stateId = session.SelectedState!.Id;
        session.SelectedCharacterName = "Алиса";
        session.SelectedStateName = "sad";
        session.SelectedStateImage = secondImage;

        Assert.AreEqual(characterId, session.SelectedCharacter.Id);
        Assert.AreEqual(stateId, session.SelectedState.Id);
        Assert.AreEqual(stateId, session.SelectedDefaultStateId);
        Assert.AreEqual(secondImage.Id, session.SelectedStateImageId);
        Assert.AreEqual("Алиса", session.Project!.Characters.Single().Name);
        Assert.AreEqual("sad", session.Project.Characters.Single().States.Single().Name);

        session.SelectedStateImage = null;
        Assert.AreEqual(secondImage.Id, session.SelectedStateImageId,
            "A transient null selection from ComboBox refresh must not erase the stable image reference.");

        session.Undo();

        Assert.AreEqual(characterId, session.SelectedCharacter!.Id);
        Assert.AreEqual(stateId, session.SelectedState!.Id);
        Assert.AreEqual(firstImageId, session.SelectedStateImageId);
        session.Redo();
        Assert.AreEqual(secondImage.Id, session.SelectedStateImageId);
    }

    [TestMethod]
    public async Task DeleteDefaultState_SelectsRemainingDefault_AndUndoRestoresSameIds()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "sprite.png", SKColors.DarkCyan);
        var session = await NewSessionAsync(new Dialogs
        {
            SavePath = Path.Combine(directory.Path, "story.novell"),
            ImagePath = source
        });
        await session.ImportImageAsync();
        session.AddCharacter();
        session.AddCharacterState();
        var firstId = session.SelectedState!.Id;
        session.AddCharacterState();
        var secondId = session.SelectedState!.Id;
        session.SelectedState = session.SelectedCharacter!.States.Single(state => state.Id == firstId);

        session.DeleteSelectedCharacterState();

        Assert.AreEqual(1, session.SelectedCharacter!.States.Count);
        Assert.AreEqual(secondId, session.SelectedDefaultStateId);
        Assert.IsNull(session.SelectedState, "Deleting the selected row must not retain a stale view-model reference.");

        session.Undo();

        Assert.AreEqual(2, session.SelectedCharacter!.States.Count);
        Assert.AreEqual(firstId, session.SelectedDefaultStateId);
        Assert.AreEqual(firstId, session.SelectedState!.Id);
        Assert.IsTrue(session.SelectedCharacter.States.Any(state => state.Id == secondId));
    }

    [TestMethod]
    public async Task BlankCharacterAndStateNames_AreSafeDraftsThatDisableSaving()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "sprite.png", SKColors.IndianRed);
        var dialogs = new Dialogs
        {
            SavePath = Path.Combine(directory.Path, "story.novell"),
            ImagePath = source
        };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        session.AddCharacter();
        session.AddCharacterState();

        session.SelectedCharacterName = "";

        Assert.AreEqual("", session.SelectedCharacter!.Name);
        Assert.IsFalse(session.AreDocumentsValid);
        Assert.IsFalse(session.SaveCommand.CanExecute(null));
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.CharacterValidation));
        session.SelectedCharacterName = "Героиня";
        session.SelectedStateName = "   ";
        Assert.AreEqual("   ", session.SelectedState!.Name);
        Assert.IsFalse(session.AreDocumentsValid);
        Assert.IsFalse(session.SaveCommand.CanExecute(null));
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.CharacterStateValidation));
        await session.SaveAsync();
        Assert.AreEqual(1, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task DanglingStateImage_RemainsEditableAndShowsDiagnostic()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "dangling-character.novell");
        var state = new NovellMaker.Core.Characters.CharacterState(Guid.NewGuid(), "missing", Guid.NewGuid());
        var character = NovellMaker.Core.Characters.NovelCharacter.Create("Герой") with
        {
            States = [state],
            DefaultStateId = state.Id
        };
        await new JsonProjectStore().SaveAsync(path, NovelProject.Create("Проект") with { Characters = [character] });
        var dialogs = new Dialogs { OpenPath = path };
        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());

        await session.OpenProjectAsync();
        session.OpenCharacterLibrary();
        session.SelectedCharacter = session.Characters.Single();

        Assert.AreEqual(state.Id, session.SelectedState!.Id);
        Assert.AreEqual(state.ImageId, session.SelectedStateImageId);
        Assert.IsNull(session.SelectedStateImage);
        StringAssert.Contains(session.CharacterStateStatus, "не найдено");
    }

    [TestMethod]
    public async Task SaveAndReopen_PreservesCharacterStatesDefaultAndImageReferences()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "characters.novell");
        var source = CreatePng(directory.Path, "hero.png", SKColors.Goldenrod);
        var dialogs = new Dialogs { SavePath = path, ImagePath = source };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        var imageId = session.SelectedImage!.Id;
        session.AddCharacter();
        session.SelectedCharacterName = "Герой";
        session.AddCharacterState();
        session.SelectedStateName = "neutral";
        var characterId = session.SelectedCharacter!.Id;
        var stateId = session.SelectedState!.Id;

        await session.SaveAsync();

        dialogs.OpenPath = path;
        var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        await reopened.OpenProjectAsync();
        reopened.OpenCharacterLibrary();
        reopened.SelectedCharacter = reopened.Characters.Single();

        Assert.AreEqual(characterId, reopened.SelectedCharacter.Id);
        Assert.AreEqual("Герой", reopened.SelectedCharacter.Name);
        Assert.AreEqual(stateId, reopened.SelectedDefaultStateId);
        Assert.AreEqual(stateId, reopened.SelectedState!.Id);
        Assert.AreEqual("neutral", reopened.SelectedState.Name);
        Assert.AreEqual(imageId, reopened.SelectedStateImageId);
        Assert.IsTrue(string.IsNullOrWhiteSpace(reopened.CharacterStateStatus));
    }

    [TestMethod]
    public async Task DialogueAssignment_SurvivesCharacterStateRenameAndProjectReopenByStableIds()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "dialogue-character.novell");
        var source = CreatePng(directory.Path, "speaker.png", SKColors.SeaGreen);
        var dialogs = new Dialogs { SavePath = path, ImagePath = source };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        session.AddCharacter();
        session.AddCharacterState();
        var characterId = session.SelectedCharacter!.Id;
        var stateId = session.SelectedState!.Id;
        session.AddScene();
        var sceneId = session.ActiveScene!.Id;
        session.ActiveScene.AddLine();
        session.ActiveScene.LineText = "Проверка персонажа";
        session.ActiveScene.SelectedSpeaker = session.ActiveScene.SpeakerOptions.Single(option => option.Id == characterId);
        session.ActiveScene.SelectedCharacterState = session.ActiveScene.StateOptions.Single(option => option.Id == stateId);

        session.OpenCharacterLibrary();
        session.SelectedCharacter = session.Characters.Single(character => character.Id == characterId);
        session.SelectedState = session.SelectedCharacter.States.Single(state => state.Id == stateId);
        session.SelectedCharacterName = "Переименованный герой";
        session.SelectedStateName = "happy";
        session.OpenScene(sceneId);

        Assert.AreEqual(characterId, session.ActiveScene!.SelectedLine!.Model.SpeakerId);
        Assert.AreEqual(stateId, session.ActiveScene.SelectedLine.Model.CharacterStateId);
        Assert.AreEqual("Переименованный герой", session.ActiveScene.SelectedSpeaker!.Name);
        Assert.AreEqual("happy", session.ActiveScene.SelectedCharacterState!.Name);
        Assert.AreEqual("Переименованный герой", session.ActiveScene.SampleFrame!.SpeakerName);
        Assert.AreEqual(characterId, session.ActiveScene.SampleFrame.Characters.Single().CharacterId);

        await session.SaveAsync();
        dialogs.OpenPath = path;
        var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        await reopened.OpenProjectAsync();
        reopened.OpenScene(sceneId);

        Assert.AreEqual(characterId, reopened.ActiveScene!.SelectedLine!.Model.SpeakerId);
        Assert.AreEqual(stateId, reopened.ActiveScene.SelectedLine.Model.CharacterStateId);
        Assert.AreEqual("Переименованный герой", reopened.ActiveScene.SampleFrame!.SpeakerName);
        Assert.AreEqual(stateId, reopened.ActiveScene.SampleFrame.Characters.Single().StateId);
    }

    [TestMethod]
    public async Task Placement_InheritanceResetDuplicateUndoAndReopenPreserveStableCommands()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "sprite.png", SKColors.SeaGreen);
        var dialogs = new Dialogs { SavePath = Path.Combine(directory.Path, "placement.novell"), ImagePath = source };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        session.AddCharacter();
        session.AddCharacterState();
        var id = session.SelectedCharacter!.Id;
        session.AddScene();
        var scene = session.ActiveScene!;
        scene.AddLine();
        scene.SelectedSpeaker = scene.SpeakerOptions.Single(option => option.Id == id);
        var firstId = scene.SelectedLine!.Id;
        scene.SetPlacement(new(0.25, 0.9, 0.8, 3));
        Assert.AreEqual(25m, scene.PlacementX);
        session.Undo();
        Assert.HasCount(0, scene.SelectedLine!.Model.Placements);
        session.Redo();
        Assert.AreEqual(25m, scene.PlacementX);
        scene.DuplicateLine();
        Assert.AreNotEqual(firstId, scene.SelectedLine!.Id);
        Assert.AreEqual(id, scene.SelectedLine.Model.Placements.Single().CharacterId);
        scene.RemovePlacementCommand();
        Assert.AreEqual(25m, scene.PlacementX);
        Assert.HasCount(0, scene.SelectedLine.Model.Placements);
        // A narrator line can reposition a non-speaking character.
        scene.SelectedSpeaker = scene.SpeakerOptions.Single(option => option.Id is null);
        scene.PlacementX = 60;
        scene.PlacementX = 65;
        session.Undo();
        Assert.AreEqual(25m, scene.PlacementX, "Sequential numeric edits form a single undo operation.");
        session.Redo();
        Assert.AreEqual(65m, scene.PlacementX);
        scene.SetPlacement(null);
        Assert.AreEqual(50m, scene.PlacementX);
        Assert.IsNull(scene.SelectedLine.Model.Placements.Single().Placement);
        session.Undo();
        Assert.AreEqual(65m, scene.PlacementX);
        await session.SaveAsync();
        var saved = await new JsonProjectStore().LoadAsync(dialogs.SavePath);
        Assert.AreEqual(0.65, saved.Scenes.Single().Lines[1].Placements.Single().Placement!.X, 0.0001);
        scene.SelectLine(firstId);
        Assert.AreEqual(25m, scene.PlacementX);
        scene.Navigate(1);
        Assert.AreEqual(65m, scene.PlacementX);
        scene.PlacementScale = 0;
        Assert.AreEqual(80m, scene.PlacementScale, "Invalid scale cannot enter the model.");
    }

    private static async Task<EditorSession> NewSessionAsync(Dialogs dialogs)
    {
        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        await session.NewProjectAsync();
        return session;
    }

    private static string CreatePng(string directory, string name, SKColor color)
    {
        var path = Path.Combine(directory, name);
        using var bitmap = new SKBitmap(6, 8);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private sealed class Dialogs : IEditorDialogs
    {
        public string? SavePath { get; set; }
        public string? OpenPath { get; set; }
        public string? ImagePath { get; set; }
        public List<(string Title, string Message)> Errors { get; } = [];
        public Task<string?> PickOpenProjectAsync() => Task.FromResult(OpenPath);
        public Task<string?> PickSaveProjectAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task<string?> PickImageAsync() => Task.FromResult(ImagePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string projectDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.CharacterEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
