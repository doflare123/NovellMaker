using NovellMaker.Core.Assets;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeImageCacheTests
{
    private sealed class Texture : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private static ImageAsset Asset(string name) => new(Guid.NewGuid(), name, "Assets/Images/" + name + ".png", 1, 1);
    private static RuntimeImageLoader.Decoded Pixels(ImageAsset asset) => new(asset.PixelWidth, asset.PixelHeight, new byte[4]);
    private static async Task Drain(RuntimeImageCache<Texture> cache)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (cache.IsPending) { timeout.Token.ThrowIfCancellationRequested(); cache.Poll(); await Task.Delay(1); }
    }

    private static async Task DrainWork(RuntimeImageCache<Texture> cache)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (cache.HasWork) { timeout.Token.ThrowIfCancellationRequested(); cache.Poll(); await Task.Delay(1); }
    }

    [TestMethod]
    public async Task ProgressiveUploadPublishesOnlyWhenCompleteAndEveryTransferFitsFrameBudget()
    {
        var image = new ImageAsset(Guid.NewGuid(), "Large", "Assets/Images/large.png", 2048, 2048);
        var transfers = new List<(int Row, int Rows)>(); var texture = new Texture();
        using var cache = new RuntimeImageCache<Texture>((_, _) => new(2048, 2048, new byte[2048 * 2048 * 4]), _ => texture,
            uploadRows: (_, _, row, rows) => transfers.Add((row, rows)));
        cache.SetWanted([image]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (transfers.Count < 16)
        {
            timeout.Token.ThrowIfCancellationRequested();
            Assert.IsNull(cache.Get(image.Id)); var before = transfers.Count;
            cache.Poll(); Assert.IsTrue(transfers.Count - before <= 1);
            Assert.IsTrue(cache.LastUploadBytes <= RuntimeImageCache<Texture>.MaximumUploadBytesPerFrame);
            await Task.Delay(1);
        }
        Assert.AreSame(texture, cache.Get(image.Id)); Assert.IsFalse(cache.IsPending);
        Assert.AreEqual(16L * 1024 * 1024, cache.CachedBytes);
        Assert.IsTrue(transfers.Select((slice, i) => slice.Row == i * 128 && slice.Rows == 128).All(x => x));
    }

    [TestMethod]
    public async Task PreloadedImageIsReadyAtTransitionWithoutAnotherDecodeOrVisibleWait()
    {
        var a = Asset("a"); var b = Asset("b");
        using var cache = new RuntimeImageCache<Texture>((asset, _) => Pixels(asset), _ => new());
        cache.SetWanted([a], [b]); await DrainWork(cache);
        Assert.IsNotNull(cache.Get(a.Id)); Assert.IsNull(cache.Get(b.Id)); Assert.AreEqual(2, cache.DecodeCount);
        cache.SetWanted([b]); Assert.IsFalse(cache.IsPending); Assert.IsNotNull(cache.Get(b.Id)); Assert.AreEqual(2, cache.DecodeCount);
    }

    [TestMethod]
    public async Task PrefetchEvictsOldInactiveTexturesWithoutEndlesslyEvictingItsOtherHints()
    {
        var old = new ImageAsset(Guid.NewGuid(), "Old", "Assets/Images/old.png", 2, 1);
        var a = Asset("a"); var b = Asset("b"); var c = Asset("c");
        using var cache = new RuntimeImageCache<Texture>((asset, _) => new(asset.PixelWidth, asset.PixelHeight, new byte[asset.PixelWidth * asset.PixelHeight * 4]),
            _ => new(), capacity: 16);
        cache.SetWanted([old]); await DrainWork(cache); var oldTexture = cache.Get(old.Id)!;
        cache.SetWanted([a]); await DrainWork(cache);
        cache.SetWanted([a], [b, c]); await DrainWork(cache);
        Assert.IsTrue(oldTexture.Disposed); Assert.AreEqual(4, cache.DecodeCount); Assert.AreEqual(12L, cache.CachedBytes);
        cache.SetWanted([b, c]); Assert.IsFalse(cache.IsPending); Assert.IsNotNull(cache.Get(b.Id)); Assert.IsNotNull(cache.Get(c.Id));
    }

    [TestMethod]
    public async Task VisibleResourcesPreemptSpeculativeUploadAndReleaseStagingBytes()
    {
        var a = new ImageAsset(Guid.NewGuid(), "Speculative", "Assets/Images/a.png", 1024, 1024); var b = Asset("b");
        var staged = new List<Texture>();
        using var cache = new RuntimeImageCache<Texture>((asset, _) => new(asset.PixelWidth, asset.PixelHeight, new byte[asset.PixelWidth * asset.PixelHeight * 4]),
            _ => { var result = new Texture(); staged.Add(result); return result; }, uploadRows: (_, _, _, _) => { });
        cache.SetWanted([], [a]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (staged.Count == 0) { timeout.Token.ThrowIfCancellationRequested(); cache.Poll(); await Task.Delay(1); }
        Assert.IsFalse(cache.IsPending); Assert.AreEqual(4L * 1024 * 1024, cache.CachedBytes);
        cache.SetWanted([b], [a]); Assert.IsTrue(staged[0].Disposed); Assert.AreEqual(0L, cache.CachedBytes);
        await Drain(cache); Assert.IsNotNull(cache.Get(b.Id)); Assert.IsFalse(cache.IsPending);
    }

    [TestMethod]
    public async Task FailedTransferDisposesStagingTextureAndDoesNotPublishPartialImage()
    {
        var a = Asset("a"); var texture = new Texture();
        using var cache = new RuntimeImageCache<Texture>((asset, _) => Pixels(asset), _ => texture,
            uploadRows: (_, _, _, _) => throw new InvalidOperationException("GPU failure"));
        cache.SetWanted([a]); await Drain(cache);
        Assert.IsTrue(texture.Disposed); Assert.AreEqual(0L, cache.CachedBytes); Assert.IsNull(cache.Get(a.Id));
        StringAssert.Contains(cache.Diagnostic(a.Id)!, "GPU failure");
    }

    [TestMethod]
    public async Task DelayedDecodeDoesNotBlockHostAndStaleCompletionNeverUploads()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Asset("a"); var b = Asset("b"); var uploads = 0; var decodes = 0;
        using var cache = new RuntimeImageCache<Texture>((asset, token) =>
        {
            Interlocked.Increment(ref decodes);
            if (asset == a) { started.SetResult(); release.Wait(token); }
            return Pixels(asset);
        }, _ => { uploads++; return new(); });
        try
        {
            cache.SetWanted([a]); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cache.Poll(); Assert.IsNull(cache.Get(a.Id)); Assert.AreEqual(0, uploads);
            cache.SetWanted([b]); Assert.AreEqual(1, decodes);
        }
        finally { release.Set(); }
        await Drain(cache);
        Assert.IsNull(cache.Get(a.Id)); Assert.IsNotNull(cache.Get(b.Id)); Assert.AreEqual(1, uploads); Assert.AreEqual(2, decodes);
    }

    [TestMethod]
    public async Task ReturningToCachedImageAvoidsDecodeAndEvictsLeastRecentlyUsedInactiveTexture()
    {
        var a = Asset("a"); var b = Asset("b"); var c = Asset("c"); var decodes = 0;
        using var cache = new RuntimeImageCache<Texture>((asset, _) => { decodes++; return Pixels(asset); }, _ => new(), capacity: 8);
        cache.SetWanted([a]); await Drain(cache); var ta = cache.Get(a.Id)!;
        cache.SetWanted([b]); await Drain(cache); var tb = cache.Get(b.Id)!;
        cache.SetWanted([a]); await Drain(cache);
        Assert.AreSame(ta, cache.Get(a.Id)); Assert.AreEqual(2, decodes);
        cache.SetWanted([a, c]); await Drain(cache);
        Assert.IsFalse(ta.Disposed); Assert.IsTrue(tb.Disposed); Assert.AreEqual(8L, cache.CachedBytes);
        cache.Dispose(); Assert.IsTrue(ta.Disposed); Assert.AreEqual(0L, cache.CachedBytes);
    }

    [TestMethod]
    public async Task VisibleBudgetAndDecodeFailuresAreDiagnosedWithoutRepeatedWorkAndRecoverOnChange()
    {
        var a = Asset("a"); var b = Asset("b"); var decodes = 0;
        using var cache = new RuntimeImageCache<Texture>((asset, _) =>
        { decodes++; if (asset == a) throw new InvalidDataException("broken PNG"); return Pixels(asset); }, _ => new(), capacity: 4);
        cache.SetWanted([a, b]); await Drain(cache);
        StringAssert.Contains(cache.Diagnostic(a.Id)!, "broken PNG"); Assert.IsNotNull(cache.Diagnostic(b.Id));
        for (var i = 0; i < 10; i++) { cache.SetWanted([a, b]); cache.Poll(); }
        Assert.AreEqual(1, decodes);
        cache.SetWanted([b]); await Drain(cache); Assert.IsNotNull(cache.Get(b.Id)); Assert.IsNull(cache.Diagnostic(a.Id));
    }

    [TestMethod]
    public async Task PollUploadsAtMostOneTextureAndOnlyAfterHostPoll()
    {
        var uploads = 0;
        using var cache = new RuntimeImageCache<Texture>((asset, _) => Pixels(asset), _ => { uploads++; return new(); });
        cache.SetWanted([Asset("a"), Asset("b"), Asset("c")]);
        await Task.Delay(20); Assert.AreEqual(0, uploads);
        while (cache.IsPending) { var before = uploads; cache.Poll(); Assert.IsTrue(uploads - before <= 1); await Task.Delay(1); }
        Assert.AreEqual(3, uploads);
    }

    [TestMethod]
    public async Task DisposeCancelsPendingDecodeAndNeverUploadsItsLateResult()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        var cache = new RuntimeImageCache<Texture>((asset, token) =>
        {
            started.SetResult();
            try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return Pixels(asset); }
            finally { finished.SetResult(); }
        }, _ => { uploads++; return new(); });
        cache.SetWanted([Asset("a")]); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cache.Dispose(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, uploads); Assert.IsFalse(cache.IsPending); cache.Dispose();
    }
}
