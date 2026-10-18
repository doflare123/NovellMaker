using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Assets;
using SkiaSharp;

namespace NovellMaker.Runtime;

internal static class RuntimeImageLoader
{
    private const long MaximumEncodedBytes = 64L * 1024 * 1024;

    public static Texture2D Load(
        GraphicsDevice graphicsDevice,
        string assetsRoot,
        ImageAsset asset)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
        ArgumentNullException.ThrowIfNull(asset);

        var path = AssetPath.Resolve(assetsRoot, asset.RelativePath);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Файл изображения не найден: {asset.RelativePath}", path);
        }

        if (file.Length is < 1 or > MaximumEncodedBytes)
        {
            throw new InvalidDataException(
                $"Размер файла изображения должен быть от 1 байта до {MaximumEncodedBytes / 1024 / 1024} МиБ: " +
                asset.RelativePath);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
        {
            throw new InvalidDataException($"Поддерживаются только PNG и JPEG: {asset.RelativePath}");
        }

        EnsureExtensionMatches(asset.RelativePath, codec.EncodedFormat);
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width is < 1 or > ImageAsset.MaximumDimension ||
            height is < 1 or > ImageAsset.MaximumDimension ||
            (long)width * height > ImageAsset.MaximumPixelCount)
        {
            throw new InvalidDataException(
                $"Размер изображения {width}×{height} превышает допустимые ограничения: {asset.RelativePath}");
        }

        if (width != asset.PixelWidth || height != asset.PixelHeight)
        {
            throw new InvalidDataException(
                $"Размер файла {width}×{height} не совпадает с метаданными " +
                $"{asset.PixelWidth}×{asset.PixelHeight}: {asset.RelativePath}");
        }

        var imageInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(imageInfo))
        {
            throw new InvalidDataException($"Не удалось выделить память для изображения: {asset.RelativePath}");
        }

        var decodeResult = codec.GetPixels(imageInfo, bitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success)
        {
            throw new InvalidDataException(
                $"Изображение повреждено или обрезано ({decodeResult}): {asset.RelativePath}");
        }

        var bytes = new byte[checked(width * height * 4)];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        var texture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color);
        try
        {
            texture.SetData(bytes);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private static void EnsureExtensionMatches(string relativePath, SKEncodedImageFormat format)
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
                $"Расширение ресурса не соответствует содержимому файла: {relativePath}");
        }
    }
}
