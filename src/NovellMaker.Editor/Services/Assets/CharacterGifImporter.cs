using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using SkiaSharp;

namespace NovellMaker.Editor.Services.Assets;

public sealed record CharacterGifImport(int Width, int Height, ImmutableArray<ImageAsset> Images, CharacterClip Clip);

public static class CharacterGifImporter
{
    private static readonly SemaphoreSlim Slots = new(1, 1);
    public static async Task<CharacterGifImport> ImportAsync(string directory, string path, CancellationToken token = default)
    {
        await Slots.WaitAsync(token);
        try { return await Task.Run(() => Import(directory, path, token), token); }
        finally { Slots.Release(); }
    }

    private static CharacterGifImport Import(string directory, string path, CancellationToken token)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (new FileInfo(path).Length > ImageAssetService.MaximumCompressedBytes) throw new ArgumentException("GIF превышает 64 МиБ.");
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new ArgumentException("Не удалось прочитать GIF.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Gif) throw new ArgumentException("Выберите файл GIF.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > 4096 || info.Height > 4096 ||
            codec.FrameCount is < 1 or > 512 || (long)info.Width * info.Height * codec.FrameCount > 64_000_000)
            throw new ArgumentException("GIF: лимит 4096 × 4096, 512 кадров, 64 Мп суммарно.");
        var created = new List<string>();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Assets", "Images"));
            var images = ImmutableArray.CreateBuilder<ImageAsset>();
            var frames = ImmutableArray.CreateBuilder<CharacterFrame>();
            using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var frameInfo = codec.FrameInfo;
            for (var index = 0; index < codec.FrameCount; index++)
            {
                token.ThrowIfCancellationRequested();
                // PriorFrame = -1 asks Skia to compose dependencies and disposal, including RestorePrevious.
                if (codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(index, -1)) != SKCodecResult.Success)
                    throw new ArgumentException($"Повреждён кадр GIF {index + 1}.");
                var id = Guid.NewGuid(); var relative = $"Assets/Images/{id:N}.png";
                var full = AssetPath.Resolve(directory, relative);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                created.Add(full);
                using (var output = new FileStream(full, FileMode.CreateNew)) data.SaveTo(output);
                images.Add(new(id, $"{Path.GetFileNameWithoutExtension(path)} {index + 1}", relative, info.Width, info.Height));
                frames.Add(new(id, Math.Max(0.01, frameInfo[index].Duration / 1000d)));
            }
            var clip = new CharacterClip(Guid.NewGuid(), Path.GetFileNameWithoutExtension(path), frames.Sum(f => f.Duration),
                codec.RepetitionCount != 0, [], frames.ToImmutable(), Math.Max(0, codec.RepetitionCount));
            return new(info.Width, info.Height, images.ToImmutable(), clip);
        }
        catch
        {
            foreach (var file in created) File.Delete(file);
            throw;
        }
    }
}
