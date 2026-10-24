using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NovellMaker.Core.Assets;
using SkiaSharp;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeImageMeasurementTests
{
    [TestMethod]
    [TestCategory("FramePreparationMeasurement")]
    public async Task MeasureImageDecodeAndReturningToCachedImage()
    {
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_FRAME_BENCHMARK_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Enable scripts/check.ps1 -MeasureFramePreparation.");
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        foreach (var (width, height) in new[] { (1920, 1080), (4096, 4096) })
        {
            var name = $"Assets/Images/measure-{width}.png";
            Directory.CreateDirectory(Path.Combine(directory, "Assets", "Images"));
            using (var bitmap = new SKBitmap(width, height))
            {
                bitmap.Erase(SKColors.CornflowerBlue);
                using var canvas = new SKCanvas(bitmap);
                using var paint = new SKPaint();
                for (var y = 0; y < height; y += 16)
                { paint.Color = new SKColor((byte)(y % 251), (byte)(y % 239), (byte)(y % 227)); canvas.DrawRect(0, y, width, 8, paint); }
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(directory, name)); encoded.SaveTo(file);
            }
            var asset = new ImageAsset(Guid.NewGuid(), name, name, width, height);
            var samples = new List<double>();
            for (var i = 0; i < 13; i++)
            {
                var timer = Stopwatch.StartNew();
                var decoded = await Task.Run(() => RuntimeImageLoader.Decode(directory, asset));
                var elapsed = timer.Elapsed.TotalMilliseconds;
                Assert.AreEqual((long)width * height * 4, decoded.Pixels.LongLength);
                if (i >= 3) samples.Add(elapsed);
            }
            var decodes = 0;
            using var cache = new RuntimeImageCache<Texture>((image, token) =>
            { decodes++; return RuntimeImageLoader.Decode(directory, image, token); }, _ => new());
            cache.SetWanted([asset]);
            while (cache.IsPending) { cache.Poll(); await Task.Delay(1); }
            var hits = new List<double>();
            for (var i = 0; i < 100; i++)
            {
                cache.SetWanted([]);
                var timer = Stopwatch.StartNew(); cache.SetWanted([asset]); cache.Poll();
                hits.Add(timer.Elapsed.TotalMilliseconds);
                Assert.IsNotNull(cache.Get(asset.Id));
            }
            Assert.AreEqual(1, decodes);
            cases.Add(new { Width = width, Height = height, EncodedBytes = new FileInfo(Path.Combine(directory, name)).Length,
                DecodedBytes = (long)width * height * 4, DecodeMedianMilliseconds = samples.Order().ElementAt(5),
                DecodeP95Milliseconds = samples.Max(), CachedReturnMedianMilliseconds = hits.Order().ElementAt(50), SamplesMilliseconds = samples });
        }
        File.WriteAllText(Path.Combine(directory, "image-decode.json"), JsonSerializer.Serialize(new {
            MeasuredAtUtc = DateTimeOffset.UtcNow, Framework = RuntimeInformation.FrameworkDescription,
            Note = "Generated striped PNG; warm OS caches, three warmups, ten decode samples; includes Task scheduling, file read and RGBA premultiplied decode/copy. Cache samples use fake GPU upload. Excludes actual GPU upload/rendering; no FPS guarantee. No timing assertions.",
            Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class Texture : IDisposable { public void Dispose() { } }
}
