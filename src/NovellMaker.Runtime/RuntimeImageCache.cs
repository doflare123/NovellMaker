using System.Diagnostics;
using NovellMaker.Core.Assets;

namespace NovellMaker.Runtime;

/// <summary>Host-owned LRU. One decode, one staging texture, bounded row transfers, atomic publication.</summary>
internal sealed class RuntimeImageCache<T> : IDisposable where T : class, IDisposable
{
    public const long MaximumCachedBytes = 128L * 1024 * 1024;
    public const int MaximumUploadBytesPerFrame = 1024 * 1024;
    public const long MaximumPrefetchBytes = 64L * 1024 * 1024;
    private const int MaximumEntries = 256;
    private const int MaximumPrefetchEntries = 16;
    private sealed class Entry(ImageAsset asset)
    {
        public ImageAsset Asset { get; } = asset;
        public T? Texture;
        public string? Error;
        public long Used;
        public long Bytes => (long)Asset.PixelWidth * Asset.PixelHeight * 4;
    }
    private sealed record Pending(Entry Entry, Task<RuntimeImageLoader.Decoded> Task);
    private sealed class Upload(Entry entry, RuntimeImageLoader.Decoded image, T texture)
    {
        public Entry Entry { get; } = entry;
        public RuntimeImageLoader.Decoded Image { get; } = image;
        public T Texture { get; } = texture;
        public int Row;
    }
    private readonly Func<ImageAsset, CancellationToken, RuntimeImageLoader.Decoded> decode;
    private readonly Func<RuntimeImageLoader.Decoded, T> create;
    private readonly Action<T, RuntimeImageLoader.Decoded, int, int>? uploadRows;
    private readonly long capacity;
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly HashSet<Guid> wanted = [];
    private readonly HashSet<Guid> prefetch = [];
    private readonly Dictionary<Guid, string> rejections = [];
    private readonly CancellationTokenSource lifetime = new();
    private Pending? pending;
    private Upload? uploading;
    private long clock;
    private bool disposed;
    public long CachedBytes { get; private set; }
    public int LastUploadBytes { get; private set; }
    public int LargestUploadBytes { get; private set; }
    public double MaximumAllocationMilliseconds { get; private set; }
    public double MaximumTransferMilliseconds { get; private set; }
    public int DecodeCount { get; private set; }
    public bool IsPending => wanted.Any(id => entries.TryGetValue(id, out var e) && e.Texture is null && e.Error is null);
    public bool HasWork => pending is not null || uploading is not null || IsPending ||
        prefetch.Any(id => entries.TryGetValue(id, out var e) && e.Texture is null && e.Error is null);

    public RuntimeImageCache(Func<ImageAsset, CancellationToken, RuntimeImageLoader.Decoded> decode,
        Func<RuntimeImageLoader.Decoded, T> create, long capacity = MaximumCachedBytes,
        Action<T, RuntimeImageLoader.Decoded, int, int>? uploadRows = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.decode = decode; this.create = create; this.capacity = capacity; this.uploadRows = uploadRows;
    }

    public T? Get(Guid id) => wanted.Contains(id) && entries.TryGetValue(id, out var e) ? e.Texture : null;
    public string? Diagnostic(Guid id) => rejections.GetValueOrDefault(id) ??
        (wanted.Contains(id) && entries.TryGetValue(id, out var e) ? e.Error : null);
    private bool Requested(Entry entry) => (wanted.Contains(entry.Asset.Id) || prefetch.Contains(entry.Asset.Id)) &&
        entries.GetValueOrDefault(entry.Asset.Id) == entry;

    // Called on the host whenever the visible frame changes; never reads a file or waits for a task.
    public void SetWanted(IEnumerable<ImageAsset> assets, IEnumerable<ImageAsset>? hints = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        wanted.Clear(); prefetch.Clear(); rejections.Clear();
        var reserved = 0L;
        foreach (var asset in assets)
        {
            if (wanted.Contains(asset.Id) || rejections.ContainsKey(asset.Id)) continue;
            var bytes = (long)asset.PixelWidth * asset.PixelHeight * 4;
            if (wanted.Count >= MaximumEntries || reserved + bytes > capacity)
            { rejections[asset.Id] = "превышен бюджет кэша изображений (128 МиБ / 256 ресурсов)."; continue; }
            reserved += bytes;
            wanted.Add(asset.Id);
            if (!entries.TryGetValue(asset.Id, out var entry)) entries.Add(asset.Id, entry = new(asset));
            entry.Used = ++clock;
        }
        var speculative = 0L;
        foreach (var asset in (hints ?? []).Take(MaximumPrefetchEntries))
        {
            if (wanted.Contains(asset.Id) || prefetch.Contains(asset.Id) || rejections.ContainsKey(asset.Id)) continue;
            var bytes = (long)asset.PixelWidth * asset.PixelHeight * 4;
            if (speculative + bytes > MaximumPrefetchBytes || reserved + bytes > capacity) continue;
            reserved += bytes; speculative += bytes; prefetch.Add(asset.Id);
            entries.TryAdd(asset.Id, new(asset));
        }
        // Keep in-flight work so a quick departure/return reuses the same decode.
        foreach (var pair in entries.Where(p => !wanted.Contains(p.Key) && !prefetch.Contains(p.Key) && p.Value.Texture is null &&
            p.Value != pending?.Entry && p.Value != uploading?.Entry).ToArray())
            entries.Remove(pair.Key);
        if (uploading is { } staging && (!Requested(staging.Entry) || !wanted.Contains(staging.Entry.Asset.Id) && IsPending)) CancelUpload();
        while (entries.Count > MaximumEntries) EvictOldest();
        StartNext();
    }

    public void Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LastUploadBytes = 0;
        var allocated = false;
        if (pending is { Task.IsCompleted: true } work)
        {
            pending = null;
            try
            {
                var image = work.Task.GetAwaiter().GetResult(); // Completed task only.
                if (Requested(work.Entry) && (wanted.Contains(work.Entry.Asset.Id) || !IsPending))
                {
                    if (image.Width != work.Entry.Asset.PixelWidth || image.Height != work.Entry.Asset.PixelHeight ||
                        image.Pixels.LongLength != work.Entry.Bytes) throw new InvalidDataException("Некорректный декодированный ресурс.");
                    while (CachedBytes + work.Entry.Bytes > capacity) EvictOldest(work.Entry, protectPrefetch: true);
                    var timer = Stopwatch.StartNew();
                    var texture = create(image);
                    MaximumAllocationMilliseconds = Math.Max(MaximumAllocationMilliseconds, timer.Elapsed.TotalMilliseconds);
                    CachedBytes += work.Entry.Bytes;
                    if (uploadRows is null) work.Entry.Texture = texture; // Atomic adapters for tests/tools.
                    else { uploading = new(work.Entry, image, texture); allocated = true; }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
            { work.Entry.Error = error.Message; }
        }
        // Allocation has its own frame; never combine it with a pixel transfer.
        if (!allocated && uploading is { } transfer)
        {
            try
            {
                var rowBytes = checked(transfer.Image.Width * 4);
                var rows = Math.Min(transfer.Image.Height - transfer.Row, Math.Max(1, MaximumUploadBytesPerFrame / rowBytes));
                var timer = Stopwatch.StartNew();
                uploadRows!(transfer.Texture, transfer.Image, transfer.Row, rows);
                MaximumTransferMilliseconds = Math.Max(MaximumTransferMilliseconds, timer.Elapsed.TotalMilliseconds);
                LastUploadBytes = checked(rows * rowBytes); LargestUploadBytes = Math.Max(LargestUploadBytes, LastUploadBytes);
                transfer.Row += rows;
                if (transfer.Row == transfer.Image.Height) { transfer.Entry.Texture = transfer.Texture; uploading = null; }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
            { transfer.Entry.Error = error.Message; CancelUpload(); }
        }
        StartNext();
    }

    private void StartNext()
    {
        if (pending is not null || uploading is not null) return;
        var entry = wanted.Concat(prefetch).Select(id => entries.GetValueOrDefault(id)).FirstOrDefault(e => e is not null && e.Texture is null && e.Error is null);
        if (entry is null) return;
        DecodeCount++;
        var token = lifetime.Token;
        pending = new(entry, Task.Run(() => decode(entry.Asset, token), token));
    }

    private void EvictOldest(Entry? excluded = null, bool protectPrefetch = false)
    {
        var oldest = entries.Where(p => !wanted.Contains(p.Key) && (!protectPrefetch || !prefetch.Contains(p.Key)) &&
            p.Value != excluded && p.Value != uploading?.Entry).MinBy(p => p.Value.Used);
        if (oldest.Value is null) throw new InvalidOperationException("Нет места в кэше изображений.");
        entries.Remove(oldest.Key);
        prefetch.Remove(oldest.Key);
        if (oldest.Value.Texture is { } texture) { texture.Dispose(); CachedBytes -= oldest.Value.Bytes; }
    }

    private void CancelUpload()
    {
        if (uploading is not { } work) return;
        uploading = null; work.Texture.Dispose(); CachedBytes -= work.Entry.Bytes;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        CancelUpload();
        foreach (var entry in entries.Values) entry.Texture?.Dispose();
        entries.Clear(); wanted.Clear(); prefetch.Clear(); rejections.Clear(); CachedBytes = 0;
        if (pending is { } work)
        {
            _ = work.Task.ContinueWith(task => { _ = task.Exception; lifetime.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pending = null;
        }
        else lifetime.Dispose();
    }
}
