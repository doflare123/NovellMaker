using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;
using SkiaSharp;
using System.Collections.Immutable;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class ImageAuthoringTests
{
    [TestMethod]
    public async Task ImportIntoUnsavedProject_WhenSavePickerCancelled_CreatesNoRegisteredOrCopiedAsset()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "source.png", SKColors.CornflowerBlue);
        var dialogs = new Dialogs { ImagePath = source };
        var session = await NewSessionAsync(dialogs);

        await session.ImportImageAsync();

        Assert.AreEqual(0, session.Images.Count);
        Assert.AreEqual(0, session.Project!.Images.Length);
        Assert.IsNull(session.ProjectDirectory);
        Assert.AreEqual(0, dialogs.Errors.Count);
    }

    [TestMethod]
    public async Task ImportRenameUndoRedoAndBackground_KeepStableAssetReferenceAfterSourceDeletion()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "фон.png", SKColors.OrangeRed);
        var projectPath = Path.Combine(directory.Path, "game.novell");
        var dialogs = new Dialogs { SavePath = projectPath, ImagePath = source };
        var session = await NewSessionAsync(dialogs);

        await session.ImportImageAsync();
        var image = session.SelectedImage!;
        var copiedPath = AssetPath.Resolve(session.ProjectDirectory!, image.RelativePath);
        File.Delete(source);
        Assert.IsTrue(File.Exists(copiedPath));
        Assert.AreEqual("3 × 2", image.Dimensions);
        session.SelectedImageName = "Основной фон";
        session.AddScene();
        var document = session.ActiveScene!;
        document.BackgroundImageId = image.Id;

        Assert.AreEqual(image.Id, document.BackgroundImageId);
        Assert.AreEqual(image.Id, document.BackgroundImage!.Id);
        Assert.IsTrue(string.IsNullOrWhiteSpace(document.BackgroundStatus));
        session.Undo();
        Assert.IsNull(document.BackgroundImageId);
        session.Redo();
        Assert.AreEqual(image.Id, document.BackgroundImageId);
        Assert.AreEqual("Основной фон", session.Project!.Images.Single().Name);
    }

    [TestMethod]
    public async Task UndoImport_KeepsCopiedFileAndRedoRestoresSameSelectedStableAsset()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "undo.png", SKColors.CadetBlue);
        var projectPath = Path.Combine(directory.Path, "story.novell");
        var session = await NewSessionAsync(new Dialogs { SavePath = projectPath, ImagePath = source });

        await session.ImportImageAsync();
        var image = session.SelectedImage!;
        var copiedPath = AssetPath.Resolve(session.ProjectDirectory!, image.RelativePath);
        session.Undo();

        Assert.AreEqual(0, session.Images.Count);
        Assert.IsTrue(File.Exists(copiedPath), "Undo changes project metadata; it must not delete a copied resource needed by redo.");
        session.Redo();

        Assert.AreEqual(image.Id, session.SelectedImage!.Id);
        Assert.AreEqual(image.RelativePath, session.SelectedImage.RelativePath);
        Assert.IsTrue(File.Exists(copiedPath));
    }

    [TestMethod]
    public async Task RenamingFilteredImage_ClearsFilterAndRetainsSelection()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "forest.png", SKColors.ForestGreen);
        var session = await NewSessionAsync(new Dialogs { SavePath = Path.Combine(directory.Path, "story.novell"), ImagePath = source });
        await session.ImportImageAsync();
        var id = session.SelectedImage!.Id;
        session.AssetFilter = "forest";

        session.SelectedImageName = "Роща";

        Assert.AreEqual("", session.AssetFilter);
        Assert.AreEqual(id, session.SelectedImage!.Id);
        Assert.AreEqual(1, session.FilteredImages.Count);
        Assert.AreEqual("Роща", session.FilteredImages.Single().Name);
    }

    [TestMethod]
    public async Task AssetSearchSaveReopenSaveAsAndFolderRelocation_PreserveEveryRegisteredImage()
    {
        using var directory = TemporaryDirectory.Create();
        var sourceOne = CreatePng(directory.Path, "forest.png", SKColors.ForestGreen);
        var sourceTwo = CreateJpeg(directory.Path, "character.jpg", SKColors.Goldenrod);
        var originalRoot = Path.Combine(directory.Path, "original");
        Directory.CreateDirectory(originalRoot);
        var originalPath = Path.Combine(originalRoot, "story.novell");
        var dialogs = new Dialogs { SavePath = originalPath, ImagePath = sourceOne };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        dialogs.ImagePath = sourceTwo;
        await session.ImportImageAsync();
        var expected = session.Project!.Images.OrderBy(image => image.Id).ToArray();

        session.AssetFilter = "FOREST";
        Assert.AreEqual(1, session.FilteredImages.Count);
        Assert.AreEqual("forest.png", session.FilteredImages.Single().Name);
        session.AssetFilter = "";
        await session.SaveAsync();
        var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        dialogs.OpenPath = originalPath;
        await reopened.OpenProjectAsync();
        Assert.AreEqual(2, reopened.Images.Count);

        var copyRoot = Path.Combine(directory.Path, "export-copy");
        Directory.CreateDirectory(copyRoot);
        var copyPath = Path.Combine(copyRoot, "story.novell");
        dialogs.SavePath = copyPath;
        await reopened.SaveAsync(saveAs: true);
        Assert.AreEqual(Path.GetFullPath(copyPath), reopened.ProjectPath);
        foreach (var image in expected)
            Assert.IsTrue(File.Exists(AssetPath.Resolve(copyRoot, image.RelativePath)), image.RelativePath);

        var relocatedRoot = Path.Combine(directory.Path, "relocated");
        Directory.Move(copyRoot, relocatedRoot);
        var relocatedPath = Path.Combine(relocatedRoot, "story.novell");
        var relocated = await new JsonProjectStore().LoadAsync(relocatedPath);
        foreach (var image in relocated.Images)
            Assert.IsTrue(File.Exists(AssetPath.Resolve(relocatedRoot, image.RelativePath)), image.RelativePath);
    }

    [TestMethod]
    public async Task BackgroundReference_SurvivesSaveAsFolderRelocationAndProvidesPreview()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "background.png", SKColors.DarkSlateBlue);
        var originalRoot = Path.Combine(directory.Path, "original");
        Directory.CreateDirectory(originalRoot);
        var dialogs = new Dialogs { SavePath = Path.Combine(originalRoot, "story.novell"), ImagePath = source };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        var imageId = session.SelectedImage!.Id;
        session.AddScene();
        session.ActiveScene!.BackgroundImageId = imageId;
        await session.SaveAsync();

        var copyRoot = Path.Combine(directory.Path, "copy");
        Directory.CreateDirectory(copyRoot);
        dialogs.SavePath = Path.Combine(copyRoot, "story.novell");
        await session.SaveAsync(saveAs: true);
        var relocatedRoot = Path.Combine(directory.Path, "relocated");
        Directory.Move(copyRoot, relocatedRoot);
        var relocatedPath = Path.Combine(relocatedRoot, "story.novell");
        dialogs.OpenPath = relocatedPath;
        var reopened = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        await reopened.OpenProjectAsync();
        reopened.OpenScene(reopened.Project!.Scenes.Single().Id);
        var scene = reopened.ActiveScene!;

        Assert.AreEqual(imageId, scene.BackgroundImageId);
        Assert.AreEqual(imageId, scene.BackgroundImage!.Id);
        Assert.IsTrue(string.IsNullOrWhiteSpace(scene.BackgroundStatus));
        var preview = await new ImageAssetService().ReadPreviewAsync(relocatedRoot, scene.BackgroundImage);
        Assert.IsTrue(preview.Length > 0);
    }

    [TestMethod]
    public async Task SaveAs_WhenManagedImageIsMissingOrDestinationCollides_PreservesOriginalPathAndDirtyDraft()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "source.png", SKColors.Blue);
        var originalRoot = Path.Combine(directory.Path, "original");
        Directory.CreateDirectory(originalRoot);
        var originalPath = Path.Combine(originalRoot, "story.novell");
        var dialogs = new Dialogs { SavePath = originalPath, ImagePath = source };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        await session.SaveAsync();
        var image = session.Project!.Images.Single();
        var originalManifest = await File.ReadAllBytesAsync(originalPath);
        session.SelectedImageName = "Несохранённое имя";
        var missingTarget = Path.Combine(directory.Path, "missing", "story.novell");
        Directory.CreateDirectory(Path.GetDirectoryName(missingTarget)!);
        File.Delete(AssetPath.Resolve(originalRoot, image.RelativePath));
        dialogs.SavePath = missingTarget;

        await session.SaveAsync(saveAs: true);

        Assert.AreEqual(Path.GetFullPath(originalPath), session.ProjectPath);
        Assert.IsTrue(session.IsDirty);
        Assert.AreEqual(1, dialogs.Errors.Count);
        CollectionAssert.AreEqual(originalManifest, await File.ReadAllBytesAsync(originalPath));
        Assert.IsFalse(File.Exists(missingTarget));

        // Restore the original managed file and prepare a different target file at the same stable path.
        var originalAssetPath = AssetPath.Resolve(originalRoot, image.RelativePath);
        File.Copy(source, originalAssetPath, overwrite: true);
        var collisionRoot = Path.Combine(directory.Path, "collision");
        Directory.CreateDirectory(collisionRoot);
        var collisionAssetPath = AssetPath.Resolve(collisionRoot, image.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(collisionAssetPath)!);
        await File.WriteAllBytesAsync(collisionAssetPath, await File.ReadAllBytesAsync(CreatePng(directory.Path, "different.png", SKColors.Red)));
        var collisionBytes = await File.ReadAllBytesAsync(collisionAssetPath);
        dialogs.SavePath = Path.Combine(collisionRoot, "story.novell");

        await session.SaveAsync(saveAs: true);

        Assert.AreEqual(Path.GetFullPath(originalPath), session.ProjectPath);
        Assert.IsTrue(session.IsDirty);
        Assert.AreEqual(2, dialogs.Errors.Count);
        CollectionAssert.AreEqual(originalManifest, await File.ReadAllBytesAsync(originalPath));
        CollectionAssert.AreEqual(collisionBytes, await File.ReadAllBytesAsync(collisionAssetPath));
        Assert.IsFalse(File.Exists(dialogs.SavePath));
    }

    [TestMethod]
    public async Task DanglingBackground_RemainsVisibleThroughCatalogChangesUntilAuthorRepairsIt()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "dangling.novell");
        var danglingId = Guid.NewGuid();
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("Сцена") with { BackgroundImageId = danglingId };
        var project = NovelProject.Create("Висячая ссылка") with { Scenes = ImmutableArray.Create(scene) };
        await new JsonProjectStore().SaveAsync(path, project);
        var source = CreatePng(directory.Path, "repair.png", SKColors.Teal);
        var dialogs = new Dialogs { OpenPath = path, SavePath = path, ImagePath = source };
        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        await session.OpenProjectAsync();
        session.OpenScene(scene.Id);
        var document = session.ActiveScene!;

        Assert.AreEqual(danglingId, document.BackgroundImageId);
        Assert.IsNull(document.BackgroundImage);
        Assert.IsFalse(string.IsNullOrWhiteSpace(document.BackgroundStatus));
        Assert.IsTrue(document.BackgroundOptions.Any(option => option.Id == danglingId));
        await session.ImportImageAsync();
        Assert.AreEqual(danglingId, document.BackgroundImageId, "Importing another asset must not silently clear a dangling scene reference.");

        document.BackgroundImageId = session.SelectedImage!.Id;
        Assert.IsTrue(string.IsNullOrWhiteSpace(document.BackgroundStatus));
        session.Undo();
        Assert.AreEqual(danglingId, document.BackgroundImageId);
    }

    [TestMethod]
    public async Task RefreshGit_RefreshesMissingImageDiagnosticWithoutDroppingStableMetadata()
    {
        using var directory = TemporaryDirectory.Create();
        var source = CreatePng(directory.Path, "missing.png", SKColors.Purple);
        var projectPath = Path.Combine(directory.Path, "story.novell");
        var dialogs = new Dialogs { SavePath = projectPath, ImagePath = source };
        var session = await NewSessionAsync(dialogs);
        await session.ImportImageAsync();
        var image = session.SelectedImage!;
        File.Delete(AssetPath.Resolve(session.ProjectDirectory!, image.RelativePath));

        await session.RefreshGitAsync();

        Assert.AreEqual(image.Id, session.Images.Single().Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.Images.Single().Status));
    }

    private static async Task<EditorSession> NewSessionAsync(Dialogs dialogs)
    {
        var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), new ImageAssetService());
        await session.NewProjectAsync();
        return session;
    }

    private static string CreatePng(string directory, string name, SKColor color) => CreateImage(directory, name, color, SKEncodedImageFormat.Png);
    private static string CreateJpeg(string directory, string name, SKColor color) => CreateImage(directory, name, color, SKEncodedImageFormat.Jpeg);
    private static string CreateImage(string directory, string name, SKColor color, SKEncodedImageFormat format)
    {
        var path = Path.Combine(directory, name);
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
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
        public Task ShowErrorAsync(string title, string message) { Errors.Add((title, message)); return Task.CompletedTask; }
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.ImageTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
