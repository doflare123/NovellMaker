using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Characters;

/// <summary>Pixel coordinates. X/Y locate the pivot in the parent's unpivoted image coordinates.</summary>
public sealed record PartTransform(double X = 0, double Y = 0, double Rotation = 0,
    double ScaleX = 1, double ScaleY = 1, double Opacity = 1)
{
    [JsonIgnore] public bool IsValid => new[] { X, Y, Rotation, ScaleX, ScaleY, Opacity }.All(double.IsFinite) &&
        Math.Abs(X) <= 32768 && Math.Abs(Y) <= 32768 && Math.Abs(Rotation) <= 36000 &&
        ScaleX is >= 0.01 and <= 10 && ScaleY is >= 0.01 and <= 10 && Opacity is >= 0 and <= 1;
}

public sealed record CharacterPart(Guid Id, string Name, Guid ImageId, Guid? ParentId,
    double PivotX, double PivotY, int Layer, PartTransform Transform);
public sealed record PartKeyframe(double Time, PartTransform Transform, Guid? ImageId = null, bool Hold = false);
public sealed record PartTrack(Guid PartId, ImmutableArray<PartKeyframe> Keys);
public sealed record CharacterFrame(Guid ImageId, double Duration);
public sealed record CharacterClip(Guid Id, string Name, double Duration, bool Loop,
    ImmutableArray<PartTrack> Tracks, ImmutableArray<CharacterFrame> Frames, int RepeatCount = 0);
public sealed record CharacterSprite(Guid ImageId, Matrix3x2 World, double Opacity, int Layer);

/// <summary>Immutable authoring data; evaluated by the same pure sampler in editor and game.</summary>
public sealed record CharacterVisual(int Width, int Height, ImmutableArray<CharacterPart> Parts,
    ImmutableArray<CharacterClip> Clips, Guid? DefaultClipId)
{
    public IEnumerable<Guid> ImageIds() => Parts.Select(p => p.ImageId)
        .Concat(Clips.SelectMany(c => c.Frames.Select(f => f.ImageId)))
        .Concat(Clips.SelectMany(c => c.Tracks).SelectMany(t => t.Keys).Select(k => k.ImageId).OfType<Guid>()).Distinct();

    public bool ContentEquals(CharacterVisual? other) => other is not null &&
        JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);

    /// <summary>Speculative order only. The runtime cache applies its own entry/byte budget.</summary>
    public IEnumerable<Guid> UpcomingImages(double elapsed)
    {
        var clip = Clips.FirstOrDefault(c => c.Id == DefaultClipId);
        if (clip is null) yield break;
        var time = ClipTime(clip, elapsed);
        if (!clip.Frames.IsEmpty)
        {
            var index = 0; var end = clip.Frames[0].Duration;
            while (index < clip.Frames.Length - 1 && time >= end) end += clip.Frames[++index].Duration;
            for (var offset = 1; offset < clip.Frames.Length; offset++)
            {
                var next = index + offset;
                if (!clip.Loop && next >= clip.Frames.Length) break;
                yield return clip.Frames[next % clip.Frames.Length].ImageId;
            }
        }
        else
            foreach (var image in clip.Tracks.SelectMany(t => t.Keys).Where(k => k.Time >= time && k.ImageId is not null)
                .OrderBy(k => k.Time).Select(k => k.ImageId!.Value).Distinct()) yield return image;
    }

    public void Validate()
    {
        void Require(bool condition, string message) { if (!condition) throw new ArgumentException("Персонаж: " + message); }
        Require(Width is > 0 and <= 8192 && Height is > 0 and <= 8192, "неверный размер холста.");
        Require(!Parts.IsDefault && !Clips.IsDefault && Parts.Length <= 64 && Clips.Length <= 128, "превышен лимит частей/клипов.");
        Require(Parts.All(p => p is not null) && Clips.All(c => c is not null), "пустая часть/клип.");
        Require(Parts.Select(p => p.Id).Concat(Clips.Select(c => c.Id)).All(id => id != Guid.Empty) &&
            Parts.Select(p => p.Id).Concat(Clips.Select(c => c.Id)).Distinct().Count() == Parts.Length + Clips.Length, "повторяющийся ID.");
        var parts = Parts.ToDictionary(p => p.Id);
        foreach (var part in Parts)
        {
            Require(!string.IsNullOrWhiteSpace(part.Name) && part.ImageId != Guid.Empty && part.Transform is { IsValid: true } &&
                double.IsFinite(part.PivotX) && double.IsFinite(part.PivotY) && Math.Abs(part.PivotX) <= 8192 && Math.Abs(part.PivotY) <= 8192 &&
                part.Layer is >= -1000 and <= 1000, "неверная часть.");
            var visited = new HashSet<Guid> { part.Id };
            for (var parent = part.ParentId; parent is { } id; parent = parts[id].ParentId)
                Require(visited.Count <= 16 && parts.ContainsKey(id) && visited.Add(id), "отсутствующий родитель или цикл иерархии.");
        }
        Require(DefaultClipId is null || Clips.Any(c => c.Id == DefaultClipId), "клип по умолчанию отсутствует.");
        foreach (var clip in Clips)
        {
            Require(!string.IsNullOrWhiteSpace(clip.Name) && double.IsFinite(clip.Duration) && clip.Duration is > 0 and <= 3600 && clip.RepeatCount is >= 0 and <= 65535 &&
                !clip.Tracks.IsDefault && !clip.Frames.IsDefault && clip.Tracks.Length <= 64 && clip.Frames.Length <= 512, "неверный клип.");
            Require(clip.Tracks.All(t => t is not null) && clip.Frames.All(f => f is not null), "пустая дорожка/кадр.");
            Require(clip.Tracks.Select(t => t.PartId).Distinct().Count() == clip.Tracks.Length, "повторяющаяся дорожка.");
            Require(clip.Frames.IsEmpty || clip.Tracks.IsEmpty, "покадровый клип не содержит дорожки частей.");
            if (!clip.Frames.IsEmpty)
                Require(clip.Frames.All(f => f.ImageId != Guid.Empty && double.IsFinite(f.Duration) && f.Duration is >= 0.01 and <= 3600) &&
                    Math.Abs(clip.Frames.Sum(f => f.Duration) - clip.Duration) < 0.00001, "неверные длительности кадров.");
            foreach (var track in clip.Tracks)
            {
                Require(parts.ContainsKey(track.PartId) && !track.Keys.IsDefaultOrEmpty && track.Keys.Length <= 1024, "неверная дорожка.");
                var previous = -1d;
                foreach (var key in track.Keys)
                {
                    Require(key is not null && double.IsFinite(key.Time) && key.Time >= 0 && key.Time > previous && key.Time <= clip.Duration &&
                        key.Transform is { IsValid: true } && key.ImageId != Guid.Empty, "неверный ключевой кадр.");
                    previous = key!.Time;
                }
            }
        }
    }

    public ImmutableArray<CharacterSprite> Sample(double elapsed, Guid? clipId = null)
    {
        var clip = Clips.FirstOrDefault(c => c.Id == (clipId ?? DefaultClipId));
        var time = clip is null ? 0 : ClipTime(clip, elapsed);
        if (clip is { Frames.IsEmpty: false })
        {
            var at = 0d;
            foreach (var frame in clip.Frames)
            { at += frame.Duration; if (time < at) return [new(frame.ImageId, Matrix3x2.Identity, 1, 0)]; }
            return [new(clip.Frames[^1].ImageId, Matrix3x2.Identity, 1, 0)];
        }
        var cache = new Dictionary<Guid, CharacterSprite>();
        var parts = Parts.ToDictionary(p => p.Id);
        CharacterSprite Evaluate(CharacterPart part)
        {
            if (cache.TryGetValue(part.Id, out var found)) return found;
            var transform = part.Transform; var image = part.ImageId;
            var track = clip?.Tracks.FirstOrDefault(t => t.PartId == part.Id);
            if (track is not null)
            {
                PartKeyframe? left = null; PartKeyframe? right = null;
                foreach (var key in track.Keys)
                {
                    if (key.Time <= time) { left = key; if (key.ImageId is { } replacement) image = replacement; }
                    else { right = key; break; }
                }
                var from = left?.Transform ?? part.Transform;
                transform = right is null || left?.Hold == true ? from : Interpolate(from, right.Transform,
                    (time - (left?.Time ?? 0)) / (right.Time - (left?.Time ?? 0)));
            }
            var local = Matrix3x2.CreateTranslation((float)-part.PivotX, (float)-part.PivotY) *
                Matrix3x2.CreateScale((float)transform.ScaleX, (float)transform.ScaleY) *
                Matrix3x2.CreateRotation((float)(transform.Rotation * Math.PI / 180)) *
                Matrix3x2.CreateTranslation((float)transform.X, (float)transform.Y);
            var opacity = transform.Opacity;
            if (part.ParentId is { } parent)
            { var sampled = Evaluate(parts[parent]); local *= sampled.World; opacity *= sampled.Opacity; }
            return cache[part.Id] = new(image, local, opacity, part.Layer);
        }
        return [.. Parts.Select(Evaluate).OrderBy(s => s.Layer)];
    }

    private static double ClipTime(CharacterClip clip, double elapsed) =>
        !clip.Loop || clip.RepeatCount > 0 && elapsed >= clip.Duration * (clip.RepeatCount + 1)
            ? Math.Clamp(elapsed, 0, clip.Duration) : Math.Max(0, elapsed) % clip.Duration;

    private static PartTransform Interpolate(PartTransform a, PartTransform b, double t)
    {
        static double Lerp(double x, double y, double t) => x + (y - x) * t;
        return new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Rotation, b.Rotation, t),
            Lerp(a.ScaleX, b.ScaleX, t), Lerp(a.ScaleY, b.ScaleY, t), Lerp(a.Opacity, b.Opacity, t));
    }
}
