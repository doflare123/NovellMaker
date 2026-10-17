using System.Buffers.Binary;
using NovellMaker.Core.Assets;
using NovellMaker.Editor.Services.Assets;
using SkiaSharp;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class ImageAssetServiceTests
{
    [TestMethod]
    public async Task ImportAsync_ImportsRealPngAndJpegFromUnicodePaths()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Проект");
        var sources = scope.CreateDirectory("Исходники персонажей");
        var pngSource = Path.Combine(sources, "героиня без расширения.data");
        var jpegSource = Path.Combine(sources, "фон сцены.jpeg");
        WriteImage(pngSource, 37, 19, SKEncodedImageFormat.Png, SKColors.CornflowerBlue);
        WriteImage(jpegSource, 29, 17, SKEncodedImageFormat.Jpeg, SKColors.OrangeRed);
        var pngBytes = File.ReadAllBytes(pngSource);
        var jpegBytes = File.ReadAllBytes(jpegSource);
        var service = new ImageAssetService();

        var png = await service.ImportAsync(project, pngSource);
        var jpeg = await service.ImportAsync(project, jpegSource);

        Assert.AreEqual("героиня без расширения.data", png.Name);
        Assert.AreEqual(37, png.PixelWidth);
        Assert.AreEqual(19, png.PixelHeight);
        Assert.IsTrue(png.RelativePath.EndsWith(".png", StringComparison.Ordinal));
        Assert.AreEqual("фон сцены.jpeg", jpeg.Name);
        Assert.AreEqual(29, jpeg.PixelWidth);
        Assert.AreEqual(17, jpeg.PixelHeight);
        Assert.IsTrue(jpeg.RelativePath.EndsWith(".jpg", StringComparison.Ordinal));
        CollectionAssert.AreEqual(pngBytes, File.ReadAllBytes(AssetPath.Resolve(project, png.RelativePath)));
        CollectionAssert.AreEqual(jpegBytes, File.ReadAllBytes(AssetPath.Resolve(project, jpeg.RelativePath)));

        File.Delete(pngSource);
        File.Move(jpegSource, Path.Combine(sources, "перемещённый.jpeg"));
        Assert.IsTrue(File.Exists(AssetPath.Resolve(project, png.RelativePath)));
        Assert.IsTrue(File.Exists(AssetPath.Resolve(project, jpeg.RelativePath)));
    }

    [TestMethod]
    public async Task ImportAsync_UsesUniqueIdentityForDuplicateSourceNames()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Project");
        var firstDirectory = scope.CreateDirectory("first");
        var secondDirectory = scope.CreateDirectory("second");
        var first = Path.Combine(firstDirectory, "portrait.png");
        var second = Path.Combine(secondDirectory, "portrait.png");
        WriteImage(first, 8, 8, SKEncodedImageFormat.Png, SKColors.Red);
        WriteImage(second, 8, 8, SKEncodedImageFormat.Png, SKColors.Blue);
        var service = new ImageAssetService();

        var firstAsset = await service.ImportAsync(project, first);
        var secondAsset = await service.ImportAsync(project, second);

        Assert.AreEqual("portrait.png", firstAsset.Name);
        Assert.AreEqual("portrait.png", secondAsset.Name);
        Assert.AreNotEqual(firstAsset.Id, secondAsset.Id);
        Assert.AreNotEqual(firstAsset.RelativePath, secondAsset.RelativePath);
        Assert.IsTrue(File.Exists(AssetPath.Resolve(project, firstAsset.RelativePath)));
        Assert.IsTrue(File.Exists(AssetPath.Resolve(project, secondAsset.RelativePath)));
    }

    [TestMethod]
    public async Task ImportAsync_WhitespaceUnixFileNameLeavesNoCommittedCopy()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows не поддерживает имя файла из одних пробелов; случай проверяется на Unix.");
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Project");
        var source = Path.Combine(scope.Path, "   ");
        WriteImage(source, 8, 8, SKEncodedImageFormat.Png, SKColors.Blue);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new ImageAssetService().ImportAsync(project, source));
        AssertNoStagingFiles(project);
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(project, "Assets", "Images")).Count(),
            "Validation must happen before committing a managed copy.");
    }

    [TestMethod]
    public async Task ImportAsync_RejectsMissingInvalidTruncatedAndDisguisedUnsupportedFiles()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Project");
        var missing = Path.Combine(scope.Path, "missing.png");
        var invalid = Path.Combine(scope.Path, "invalid.png");
        var truncated = Path.Combine(scope.Path, "truncated.jpg");
        var unsupported = Path.Combine(scope.Path, "webp-disguised-as-png.png");
        File.WriteAllBytes(invalid, [1, 2, 3, 4, 5]);
        WriteImage(truncated, 32, 16, SKEncodedImageFormat.Jpeg, SKColors.Green);
        var validJpeg = File.ReadAllBytes(truncated);
        File.WriteAllBytes(truncated, validJpeg[..(validJpeg.Length / 2)]);
        WriteImage(unsupported, 12, 12, SKEncodedImageFormat.Webp, SKColors.Purple);
        var service = new ImageAssetService();

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => service.ImportAsync(project, missing));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(project, invalid));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(project, truncated));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(project, unsupported));
        AssertNoStagingFiles(project);
    }

    [TestMethod]
    public async Task ImportAsync_RejectsCompressedAndPixelDimensionLimitsBeforeDecode()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Project");
        var tooLargeFile = Path.Combine(scope.Path, "too-large.png");
        using (var stream = new FileStream(tooLargeFile, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength((64L * 1024 * 1024) + 1);
        }

        var tooWide = Path.Combine(scope.Path, "too-wide.png");
        var tooManyPixels = Path.Combine(scope.Path, "too-many-pixels.png");
        WritePngWithPatchedDimensions(tooWide, 8193, 1);
        WritePngWithPatchedDimensions(tooManyPixels, 8000, 4001);
        var service = new ImageAssetService();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(project, tooLargeFile));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(project, tooWide));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(project, tooManyPixels));
        AssertNoStagingFiles(project);
    }

    [TestMethod]
    public async Task ImportAsync_CancellationLeavesNoStagingOrFinalFile()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Project");
        var source = Path.Combine(scope.Path, "portrait.png");
        WriteImage(source, 128, 128, SKEncodedImageFormat.Png, SKColors.Gold);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => new ImageAssetService().ImportAsync(project, source, cancellation.Token));

        AssertNoStagingFiles(project);
        var imagesDirectory = Path.Combine(project, "Assets", "Images");
        Assert.IsFalse(Directory.Exists(imagesDirectory) && Directory.EnumerateFiles(imagesDirectory).Any());
    }

    [TestMethod]
    public async Task CopyAsync_CopiesContentSkipsIdenticalAndPreservesDifferentCollision()
    {
        using var scope = new TemporaryDirectory();
        var sourceProject = scope.CreateDirectory("SourceProject");
        var targetProject = scope.CreateDirectory("TargetProject");
        var externalSource = Path.Combine(scope.Path, "background.png");
        WriteImage(externalSource, 24, 14, SKEncodedImageFormat.Png, SKColors.DarkCyan);
        var service = new ImageAssetService();
        var image = await service.ImportAsync(sourceProject, externalSource);
        var sourceAssetPath = AssetPath.Resolve(sourceProject, image.RelativePath);
        var targetAssetPath = AssetPath.Resolve(targetProject, image.RelativePath);

        await service.CopyAsync(sourceProject, targetProject, [image]);
        CollectionAssert.AreEqual(File.ReadAllBytes(sourceAssetPath), File.ReadAllBytes(targetAssetPath));

        await service.CopyAsync(sourceProject, targetProject, [image]);
        var differentBytesPath = Path.Combine(scope.Path, "different.png");
        WriteImage(differentBytesPath, 24, 14, SKEncodedImageFormat.Png, SKColors.HotPink);
        var differentBytes = File.ReadAllBytes(differentBytesPath);
        File.WriteAllBytes(targetAssetPath, differentBytes);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => service.CopyAsync(sourceProject, targetProject, [image]));
        CollectionAssert.AreEqual(differentBytes, File.ReadAllBytes(targetAssetPath));
        AssertNoStagingFiles(targetProject);
    }

    [TestMethod]
    public async Task CopyAsync_RejectsMissingAndCorruptSourceAssets()
    {
        using var scope = new TemporaryDirectory();
        var sourceProject = scope.CreateDirectory("SourceProject");
        var targetProject = scope.CreateDirectory("TargetProject");
        var missing = new ImageAsset(
            Guid.NewGuid(),
            "missing.png",
            $"Assets/Images/{Guid.NewGuid():N}.png",
            1,
            1);
        var corrupt = new ImageAsset(
            Guid.NewGuid(),
            "corrupt.png",
            $"Assets/Images/{Guid.NewGuid():N}.png",
            1,
            1);
        var corruptPath = AssetPath.Resolve(sourceProject, corrupt.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllBytes(corruptPath, [0, 1, 2, 3]);
        var service = new ImageAssetService();

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => service.CopyAsync(sourceProject, targetProject, [missing]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.CopyAsync(sourceProject, targetProject, [corrupt]));
        AssertNoStagingFiles(targetProject);
    }

    [TestMethod]
    public async Task CopyAndPreview_SupportNestedAssetPathAfterProjectRelocation()
    {
        using var scope = new TemporaryDirectory();
        var sourceProject = scope.CreateDirectory("SourceProject");
        var targetProject = scope.CreateDirectory("TargetProject");
        var relativePath = $"Assets/Images/characters/main/{Guid.NewGuid():N}.png";
        var sourcePath = AssetPath.Resolve(sourceProject, relativePath);
        WriteImage(sourcePath, 96, 48, SKEncodedImageFormat.Png, SKColors.RoyalBlue);
        var image = new ImageAsset(Guid.NewGuid(), "main portrait", relativePath, 96, 48);
        var service = new ImageAssetService();

        var sourcePreview = await service.ReadPreviewAsync(sourceProject, image, maximumWidth: 48);
        AssertPngDimensions(sourcePreview, 48, 24);
        await service.CopyAsync(sourceProject, targetProject, [image]);

        var copiedPath = AssetPath.Resolve(targetProject, relativePath);
        CollectionAssert.AreEqual(File.ReadAllBytes(sourcePath), File.ReadAllBytes(copiedPath));
        var relocatedTarget = Path.Combine(scope.Path, "RelocatedTarget");
        Directory.Move(targetProject, relocatedTarget);
        var relocatedPreview = await service.ReadPreviewAsync(relocatedTarget, image, maximumWidth: 48);
        AssertPngDimensions(relocatedPreview, 48, 24);
    }

    [TestMethod]
    public async Task CopyAsync_DimensionMismatchPreservesExistingTargetAndCleansStaging()
    {
        using var scope = new TemporaryDirectory();
        var sourceProject = scope.CreateDirectory("SourceProject");
        var targetProject = scope.CreateDirectory("TargetProject");
        var relativePath = $"Assets/Images/nested/{Guid.NewGuid():N}.png";
        var sourcePath = AssetPath.Resolve(sourceProject, relativePath);
        var targetPath = AssetPath.Resolve(targetProject, relativePath);
        WriteImage(sourcePath, 32, 16, SKEncodedImageFormat.Png, SKColors.DarkOrange);
        WriteImage(targetPath, 31, 16, SKEncodedImageFormat.Png, SKColors.DarkViolet);
        var targetBytes = File.ReadAllBytes(targetPath);
        var incorrectMetadata = new ImageAsset(Guid.NewGuid(), "mismatch", relativePath, 31, 16);
        var service = new ImageAssetService();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.CopyAsync(sourceProject, targetProject, [incorrectMetadata]));

        CollectionAssert.AreEqual(targetBytes, File.ReadAllBytes(targetPath));
        AssertNoStagingFiles(targetProject);
    }

    [TestMethod]
    public async Task ReadPreviewAsync_ReturnsBoundedPngWithoutUpscaling()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("Project");
        var wideSource = Path.Combine(scope.Path, "wide.jpg");
        var smallSource = Path.Combine(scope.Path, "small.png");
        WriteImage(wideSource, 400, 200, SKEncodedImageFormat.Jpeg, SKColors.SteelBlue);
        WriteImage(smallSource, 40, 20, SKEncodedImageFormat.Png, SKColors.Tomato);
        var service = new ImageAssetService();
        var wide = await service.ImportAsync(project, wideSource);
        var small = await service.ImportAsync(project, smallSource);

        var scaledPreview = await service.ReadPreviewAsync(project, wide, maximumWidth: 100);
        var originalSizePreview = await service.ReadPreviewAsync(project, small, maximumWidth: 100);

        AssertPngDimensions(scaledPreview, 100, 50);
        AssertPngDimensions(originalSizePreview, 40, 20);
    }

    [TestMethod]
    public async Task ReadPreviewAsync_ReadsRelocatedProjectAndDiagnosesChangedCorruptFile()
    {
        using var scope = new TemporaryDirectory();
        var project = scope.CreateDirectory("OriginalProject");
        var source = Path.Combine(scope.Path, "character.png");
        WriteImage(source, 48, 36, SKEncodedImageFormat.Png, SKColors.MediumSeaGreen);
        var service = new ImageAssetService();
        var image = await service.ImportAsync(project, source);
        var relocatedProject = Path.Combine(scope.Path, "Перемещённый проект");
        Directory.Move(project, relocatedProject);

        var preview = await service.ReadPreviewAsync(relocatedProject, image, maximumWidth: 32);
        AssertPngDimensions(preview, 32, 24);

        File.WriteAllBytes(AssetPath.Resolve(relocatedProject, image.RelativePath), [1, 2, 3]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.ReadPreviewAsync(relocatedProject, image));
    }

    private static void WriteImage(
        string path,
        int width,
        int height,
        SKEncodedImageFormat format,
        SKColor color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality: 90);
        Assert.IsNotNull(data, $"SkiaSharp could not encode test image as {format}.");
        using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    private static void WritePngWithPatchedDimensions(string path, int width, int height)
    {
        WriteImage(path, 1, 1, SKEncodedImageFormat.Png, SKColors.Black);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        var crc = ComputeCrc32(bytes.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), crc);
        File.WriteAllBytes(path, bytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static void AssertPngDimensions(byte[] bytes, int width, int height)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        Assert.IsNotNull(codec);
        Assert.AreEqual(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.AreEqual(width, codec.Info.Width);
        Assert.AreEqual(height, codec.Info.Height);
        using var bitmap = SKBitmap.Decode(data);
        Assert.IsNotNull(bitmap, "Preview PNG must be fully decodable.");
    }

    private static void AssertNoStagingFiles(string project)
    {
        var imagesDirectory = Path.Combine(project, "Assets", "Images");
        if (Directory.Exists(imagesDirectory))
        {
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(imagesDirectory, ".staging-*.tmp").Count(),
                "Temporary staging files must be removed after failure or cancellation.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NovellMaker.ImageAssetTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
        {
            var directory = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(directory);
            return directory;
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
