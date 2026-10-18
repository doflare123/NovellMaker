using NovellMaker.Core.Assets;
using SkiaSharp;

namespace NovellMaker.Editor.Services.Assets;

public sealed class ImageAssetService : IImageAssetService
{
    internal const long MaximumCompressedBytes = 64L * 1024 * 1024;
    internal const int MaximumDimension = 8192;
    internal const long MaximumPixels = 32_000_000;

    private const string ManagedImagesDirectory = "Assets/Images";
    private static readonly SemaphoreSlim DecodeSlots = new(initialCount: 2, maxCount: 2);

    public async Task<ImageAsset> ImportAsync(
        string projectDirectory,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureProjectExists(projectDirectory);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Файл изображения не найден.", fullSourcePath);
        }

        var imagesDirectory = EnsureManagedImagesDirectory(projectDirectory);
        var stagingPath = CreateStagingPath(imagesDirectory);

        try
        {
            await CopyToNewFileAsync(fullSourcePath, stagingPath, cancellationToken)
                .ConfigureAwait(false);
            var inspection = await InspectAsync(stagingPath, cancellationToken)
                .ConfigureAwait(false);

            var id = Guid.NewGuid();
            var extension = inspection.Format == SKEncodedImageFormat.Png ? ".png" : ".jpg";
            var relativePath = $"{ManagedImagesDirectory}/{id:N}{extension}";
            AssetPath.ValidateRelativePath(relativePath);
            var destinationPath = AssetPath.Resolve(projectDirectory, relativePath);
            var image = new ImageAsset(
                id,
                Path.GetFileName(fullSourcePath),
                relativePath,
                inspection.Width,
                inspection.Height);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, destinationPath, overwrite: false);
            return image;
        }
        finally
        {
            DeleteStagingFile(stagingPath);
        }
    }

    public async Task CopyAsync(
        string sourceProjectDirectory,
        string targetProjectDirectory,
        IReadOnlyList<ImageAsset> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectDirectory);
        ArgumentNullException.ThrowIfNull(images);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureProjectExists(sourceProjectDirectory);
        EnsureProjectExists(targetProjectDirectory);
        var targetImagesDirectory = EnsureManagedImagesDirectory(targetProjectDirectory);

        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(image);
            EnsureManagedImagePath(image.RelativePath);

            var sourcePath = AssetPath.Resolve(sourceProjectDirectory, image.RelativePath);
            var destinationPath = AssetPath.Resolve(targetProjectDirectory, image.RelativePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Файл ресурса '{image.RelativePath}' не найден.",
                    sourcePath);
            }

            var stagingPath = CreateStagingPath(targetImagesDirectory);
            try
            {
                await CopyToNewFileAsync(sourcePath, stagingPath, cancellationToken)
                    .ConfigureAwait(false);
                var inspection = await InspectAsync(stagingPath, cancellationToken)
                    .ConfigureAwait(false);
                EnsureExtensionMatchesFormat(image.RelativePath, inspection.Format);
                if (inspection.Width != image.PixelWidth || inspection.Height != image.PixelHeight)
                    throw new InvalidDataException($"Размер файла ресурса '{image.RelativePath}' не совпадает с метаданными.");

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(destinationPath))
                {
                    if (!await FilesEqualAsync(stagingPath, destinationPath, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        throw new IOException(
                            $"Файл ресурса '{image.RelativePath}' уже существует и отличается от исходного.");
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                destinationPath = AssetPath.Resolve(targetProjectDirectory, image.RelativePath);
                File.Move(stagingPath, destinationPath, overwrite: false);
            }
            finally
            {
                DeleteStagingFile(stagingPath);
            }
        }
    }

    public async Task<byte[]> ReadPreviewAsync(
        string projectDirectory,
        ImageAsset image,
        int maximumWidth = 1280,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(image);
        if (maximumWidth is < 1 or > 1280)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWidth),
                maximumWidth,
                "Ширина предпросмотра должна быть от 1 до 1280 пикселей.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureProjectExists(projectDirectory);
        ImageAsset.ValidateForPersistence(image);
        EnsureManagedImagePath(image.RelativePath);
        var sourcePath = AssetPath.Resolve(projectDirectory, image.RelativePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Файл ресурса '{image.RelativePath}' не найден.",
                sourcePath);
        }

        EnsureCompressedSize(sourcePath);
        await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => DecodePreview(sourcePath, image, maximumWidth, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static string EnsureManagedImagesDirectory(string projectDirectory)
    {
        EnsureProjectExists(projectDirectory);
        var probeRelativePath = $"{ManagedImagesDirectory}/.{Guid.NewGuid():N}.png";
        var probePath = AssetPath.Resolve(projectDirectory, probeRelativePath);
        var imagesDirectory = Path.GetDirectoryName(probePath)!;
        var assetsDirectory = Path.GetDirectoryName(imagesDirectory)!;

        Directory.CreateDirectory(assetsDirectory);
        _ = AssetPath.Resolve(projectDirectory, probeRelativePath);

        Directory.CreateDirectory(imagesDirectory);
        _ = AssetPath.Resolve(projectDirectory, probeRelativePath);
        return imagesDirectory;
    }

    private static void EnsureProjectExists(string projectDirectory)
    {
        var fullProjectDirectory = Path.GetFullPath(projectDirectory);
        if (!Directory.Exists(fullProjectDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Каталог проекта не существует: '{fullProjectDirectory}'.");
        }
    }

    private static void EnsureManagedImagePath(string relativePath)
    {
        AssetPath.ValidateRelativePath(relativePath);
    }

    private static async Task CopyToNewFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        EnsureCompressedSize(sourcePath);

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        long copiedBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            copiedBytes += bytesRead;
            if (copiedBytes > MaximumCompressedBytes)
            {
                throw new InvalidDataException(
                    $"Файл изображения превышает допустимый размер {MaximumCompressedBytes / (1024 * 1024)} МиБ.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ImageInspection> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => InspectAndDecode(path, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static ImageInspection InspectAndDecode(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new InvalidDataException("Файл не является поддерживаемым изображением.");
        }

        var inspection = ValidateCodec(codec);
        using var bitmap = Decode(codec, inspection, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return inspection;
    }

    private static byte[] DecodePreview(
        string path,
        ImageAsset imageAsset,
        int maximumWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCompressedSize(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new InvalidDataException("Файл ресурса не является поддерживаемым изображением.");
        }

        var inspection = ValidateCodec(codec);
        EnsureExtensionMatchesFormat(imageAsset.RelativePath, inspection.Format);
        if (inspection.Width != imageAsset.PixelWidth || inspection.Height != imageAsset.PixelHeight)
        {
            throw new InvalidDataException(
                $"Размер файла ресурса {inspection.Width}×{inspection.Height} не совпадает с метаданными " +
                $"{imageAsset.PixelWidth}×{imageAsset.PixelHeight}.");
        }

        using var decoded = Decode(codec, inspection, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        SKBitmap preview = decoded;
        SKBitmap? resized = null;
        if (inspection.Width > maximumWidth)
        {
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(
                    inspection.Height * (double)maximumWidth / inspection.Width,
                    MidpointRounding.AwayFromZero));
            var targetInfo = new SKImageInfo(
                maximumWidth,
                targetHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul);
            resized = decoded.Resize(
                targetInfo,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized is null)
            {
                throw new InvalidDataException("Не удалось уменьшить изображение для предпросмотра.");
            }

            preview = resized;
        }

        using (resized)
        using (var previewImage = SKImage.FromBitmap(preview))
        using (var encoded = previewImage.Encode(SKEncodedImageFormat.Png, quality: 100))
        {
            if (encoded is null)
            {
                throw new InvalidDataException("Не удалось создать изображение предпросмотра.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return encoded.ToArray();
        }
    }

    private static ImageInspection ValidateCodec(SKCodec codec)
    {
        if (codec.EncodedFormat is not SKEncodedImageFormat.Png and not SKEncodedImageFormat.Jpeg)
        {
            throw new InvalidDataException("Поддерживается импорт только изображений PNG и JPEG.");
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        var pixelCount = (long)width * height;
        if (width is < 1 or > MaximumDimension ||
            height is < 1 or > MaximumDimension ||
            pixelCount > MaximumPixels)
        {
            throw new InvalidDataException(
                $"Размер изображения {width}×{height} превышает допустимые ограничения.");
        }

        return new(codec.EncodedFormat, width, height);
    }

    private static SKBitmap Decode(
        SKCodec codec,
        ImageInspection inspection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decodeInfo = new SKImageInfo(
            inspection.Width,
            inspection.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(decodeInfo))
        {
            bitmap.Dispose();
            throw new InvalidDataException("Не удалось выделить память для проверки изображения.");
        }

        var decodeResult = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException(
                $"Изображение повреждено или обрезано ({decodeResult}).");
        }

        return bitmap;
    }

    private static void EnsureExtensionMatchesFormat(
        string relativePath,
        SKEncodedImageFormat format)
    {
        var extension = Path.GetExtension(relativePath);
        var matches = format switch
        {
            SKEncodedImageFormat.Png => extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            SKEncodedImageFormat.Jpeg =>
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        if (!matches)
        {
            throw new InvalidDataException(
                $"Расширение ресурса '{relativePath}' не соответствует содержимому файла.");
        }
    }

    private static async Task<bool> FilesEqualAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        var leftLength = new FileInfo(leftPath).Length;
        var rightLength = new FileInfo(rightPath).Length;
        if (leftLength != rightLength)
        {
            return false;
        }

        await using var left = new FileStream(
            leftPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(
            rightPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];
        while (true)
        {
            var leftRead = await left.ReadAsync(leftBuffer, cancellationToken).ConfigureAwait(false);
            var rightRead = await right.ReadAsync(rightBuffer, cancellationToken).ConfigureAwait(false);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static string CreateStagingPath(string imagesDirectory) =>
        Path.Combine(imagesDirectory, $".staging-{Guid.NewGuid():N}.tmp");

    private static void EnsureCompressedSize(string path)
    {
        var sourceLength = new FileInfo(path).Length;
        if (sourceLength > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                $"Файл изображения превышает допустимый размер {MaximumCompressedBytes / (1024 * 1024)} МиБ.");
        }
    }

    private static void DeleteStagingFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // The project may have been moved while the operation was being cancelled.
        }
    }

    private sealed record ImageInspection(SKEncodedImageFormat Format, int Width, int Height);
}
