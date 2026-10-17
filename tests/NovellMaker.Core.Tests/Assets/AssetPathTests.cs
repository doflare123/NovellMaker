using NovellMaker.Core.Assets;

namespace NovellMaker.Core.Tests.Assets;

[TestClass]
public sealed class AssetPathTests
{
    [TestMethod]
    [DataRow("Assets/Images/background.png")]
    [DataRow("Assets/Images/Глава 1/фон.JPEG")]
    [DataRow("Assets/Images/characters/hero.JPG")]
    public void ValidateRelativePath_AcceptsPortableImagePaths(string path)
    {
        AssetPath.ValidateRelativePath(path);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Assets/Images")]
    [DataRow("Assets/Images/")]
    [DataRow("assets/Images/image.png")]
    [DataRow("Assets/Images/../image.png")]
    [DataRow("Assets/Images/./image.png")]
    [DataRow("Assets/Images//image.png")]
    [DataRow("Assets\\Images\\image.png")]
    [DataRow("/Assets/Images/image.png")]
    [DataRow("C:/Assets/Images/image.png")]
    [DataRow("Other/image.png")]
    [DataRow("Assets/Images/inva?lid.png")]
    [DataRow("Assets/Images/CON.png")]
    [DataRow("Assets/Images/folder/NUL.jpeg")]
    [DataRow("Assets/Images/COM¹.png")]
    [DataRow("Assets/Images/CONIN$.jpg")]
    [DataRow("Assets/Images/image.gif")]
    [DataRow("Assets/Images/image")]
    public void ValidateRelativePath_RejectsUnsafeOrUnsupportedPaths(string? path)
    {
        Assert.Throws<ArgumentException>(() => AssetPath.ValidateRelativePath(path!));
    }

    [TestMethod]
    public void Resolve_ConfinesMissingAssetPathAndSupportsRelocatingProject()
    {
        using var first = TemporaryDirectory.Create();
        using var second = TemporaryDirectory.Create();
        const string relativePath = "Assets/Images/Сцена/фон.png";

        var firstResolved = AssetPath.Resolve(first.Path, relativePath);
        var secondResolved = AssetPath.Resolve(second.Path, relativePath);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(first.Path, "Assets", "Images", "Сцена", "фон.png")),
            firstResolved);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(second.Path, "Assets", "Images", "Сцена", "фон.png")),
            secondResolved);
        Assert.AreNotEqual(firstResolved, secondResolved);
        Assert.IsFalse(Directory.Exists(Path.Combine(first.Path, "Assets")),
            "Resolve must not create asset directories.");
    }

    [TestMethod]
    public void Resolve_RejectsExistingSymlinkBelowProjectRoot_WhenEnvironmentSupportsSymlinks()
    {
        using var project = TemporaryDirectory.Create();
        using var external = TemporaryDirectory.Create();
        var assetsLink = Path.Combine(project.Path, "Assets");

        try
        {
            Directory.CreateSymbolicLink(assetsLink, external.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Среда не разрешает создание символической ссылки: {exception.Message}");
        }

        Assert.ThrowsExactly<IOException>(() =>
            AssetPath.Resolve(project.Path, "Assets/Images/background.png"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NovellMaker.AssetPath.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
