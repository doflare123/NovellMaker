using System.Collections.Immutable;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Screens.Animations;

public sealed record ScreenTween(Guid NodeId, string Property, double To, double Start, double Seconds, string Easing);
public sealed record ScreenAnimation(Guid Id, ImmutableArray<ScreenTween> Tracks)
{
    public const int MaximumTracks = 64;
    public const double MaximumSeconds = 3600;
    public double Duration => Tracks.Max(t => t.Start + t.Seconds);

    internal void Validate(ScreenDefinition definition, bool validateTimeline = true)
    {
        if (Id == Guid.Empty || Tracks.IsDefaultOrEmpty || Tracks.Length > MaximumTracks || Tracks.Any(t => t is null))
            throw new ArgumentException("Неверный ID или бюджет анимации.");
        foreach (var track in Tracks.OrderBy(t => t.Start))
        {
            if (track is null || !double.IsFinite(track.To) || !double.IsFinite(track.Start) || !double.IsFinite(track.Seconds) ||
                track.Start < 0 || track.Seconds <= 0 || track.Start + track.Seconds <= track.Start || track.Start + track.Seconds > MaximumSeconds || !Easings.Contains(track.Easing))
                throw new ArgumentException("Неверное время, easing или значение анимации.");
            var node = definition.Nodes.FirstOrDefault(n => n.Id == track.NodeId) ?? throw new ArgumentException("Чужой узел анимации.");
            var from = Read(node, track.Property);
            if (track.Property is "scale_x" or "scale_y" && Math.Sign(from) != Math.Sign(track.To))
                throw new ArgumentException("Анимация масштаба не может проходить через ноль.");
            var target = Write(node, track.Property, track.To);
            if (!target.Bounds.IsValid || !target.Geometry.IsValid) throw new ArgumentException("Недопустимое свойство анимации.");
        }
        for (var i = 0; i < Tracks.Length; i++)
            for (var j = i + 1; j < Tracks.Length; j++)
                if (Tracks[i].NodeId == Tracks[j].NodeId && Tracks[i].Property == Tracks[j].Property &&
                    Tracks[i].Start < Tracks[j].Start + Tracks[j].Seconds && Tracks[j].Start < Tracks[i].Start + Tracks[i].Seconds)
                    throw new ArgumentException("Параллельные анимации одного свойства запрещены.");
        if (validateTimeline) ScreenAnimator.ValidateTimeline(definition, this);
    }

    internal static readonly string[] Easings = ["linear", "in_quad", "out_quad", "in_out_quad", "in_cubic", "out_cubic", "in_out_cubic"];
    internal static double Ease(string name, double t) => name switch
    {
        "linear" => t, "in_quad" => t * t, "out_quad" => 1 - (1 - t) * (1 - t),
        "in_out_quad" => t < .5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2,
        "in_cubic" => t * t * t, "out_cubic" => 1 - Math.Pow(1 - t, 3),
        "in_out_cubic" => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
        _ => throw new ArgumentException("Неизвестный easing.")
    };

    internal static double Read(ScreenNode n, string property) => property switch
    {
        "x" => n.Bounds.X, "y" => n.Bounds.Y, "width" => n.Bounds.Width, "height" => n.Bounds.Height,
        "anchor_x" => n.Geometry.AnchorX, "anchor_y" => n.Geometry.AnchorY, "pivot_x" => n.Geometry.PivotX, "pivot_y" => n.Geometry.PivotY,
        "scale_x" => n.Geometry.ScaleX, "scale_y" => n.Geometry.ScaleY, "rotation" => n.Geometry.Rotation, "opacity" => n.Geometry.Opacity,
        _ => throw new ArgumentException("Свойство не поддерживает анимацию.")
    };
    internal static ScreenNode Write(ScreenNode n, string property, double v) => Patch(property, v).Apply(n);
    internal static ScreenNodePatch Patch(string property, double v) => property switch
    {
        "x" => new() { X = v }, "y" => new() { Y = v }, "width" => new() { Width = v }, "height" => new() { Height = v },
        "anchor_x" => new() { AnchorX = v }, "anchor_y" => new() { AnchorY = v }, "pivot_x" => new() { PivotX = v }, "pivot_y" => new() { PivotY = v },
        "scale_x" => new() { ScaleX = v }, "scale_y" => new() { ScaleY = v }, "rotation" => new() { Rotation = v }, "opacity" => new() { Opacity = v },
        _ => throw new ArgumentException("Свойство не поддерживает анимацию.")
    };
    internal static bool Touches(ScreenNodePatch p, string property) => property switch
    {
        "x" => p.X.HasValue, "y" => p.Y.HasValue, "width" => p.Width.HasValue, "height" => p.Height.HasValue,
        "anchor_x" => p.AnchorX.HasValue, "anchor_y" => p.AnchorY.HasValue, "pivot_x" => p.PivotX.HasValue, "pivot_y" => p.PivotY.HasValue,
        "scale_x" => p.ScaleX.HasValue, "scale_y" => p.ScaleY.HasValue, "rotation" => p.Rotation.HasValue, "opacity" => p.Opacity.HasValue, _ => false
    };
}

public sealed record ScreenAnimationResult(Guid Id, string Status, string Reason)
{
    // Trusted host causality only: never serialized to, or accepted from, the worker.
    internal int ChainDepth { get; init; }
    internal void Validate()
    {
        if (Id == Guid.Empty || Status is not ("completed" or "cancelled" or "failed") || Reason is null || Reason.Length > 128)
            throw new ArgumentException("Неверное событие анимации.");
    }
}

/// <summary>Host-thread timeline. Preparation clones state; rejection never partially starts/cancels a group.</summary>
internal sealed class ScreenAnimator
{
    private sealed class Running(ScreenAnimation animation, int chainDepth = 0)
    {
        internal readonly ScreenAnimation Animation = animation;
        internal readonly int ChainDepth = chainDepth;
        internal double Time;
        internal double?[] From = new double?[animation.Tracks.Length];
        internal Running Copy() => new(Animation, ChainDepth) { Time = Time, From = (double?[])From.Clone() };
    }
    private readonly List<Running> active = [];
    private readonly Queue<ScreenAnimationResult> results = [];
    internal int Reserved => active.Count + results.Count;
    internal bool Busy => Reserved != 0;
    internal int ActiveCount => active.Count;
    internal static void ValidateTimeline(ScreenDefinition definition, ScreenAnimation animation)
    {
        var group = new Running(animation with { Tracks = [.. animation.Tracks.OrderBy(t => t.Start)] });
        Advance(group, ref definition, animation.Duration);
    }
    internal ScreenAnimator Prepare(ScreenDefinition before, ImmutableArray<ScreenChange> changes, int chainDepth = 0)
    {
        // The usual non-animated handler keeps its prevalidated background batch:
        // do not replay text/tree preparation on the host just to retain an empty timeline.
        if (!Busy && !changes.Any(c => c.Animation is not null)) return this;
        var copy = new ScreenAnimator(); copy.active.AddRange(active.Select(a => a.Copy()));
        foreach (var result in results) copy.results.Enqueue(result);
        var current = before;
        foreach (var change in changes)
        {
            current = ScreenLuaProtocol.Prepare(current, [change]).Definition;
            if (change.Animation is { } animation)
            {
                animation.Validate(current);
                CheckDepth(chainDepth + 1);
                if (copy.active.Any(a => a.Animation.Id == animation.Id) || copy.results.Any(r => r.Id == animation.Id))
                    throw new InvalidOperationException("Повторный ID анимации.");
                copy.Cancel(a => a.Animation.Tracks.Any(t => animation.Tracks.Any(n => n.NodeId == t.NodeId && n.Property == t.Property)), "replaced", chainDepth);
                // Parallel children may flatten later property intervals before earlier ones.
                // Apply expired intervals first so they cannot overwrite the currently active track.
                copy.active.Add(new(animation with { Tracks = [.. animation.Tracks.OrderBy(t => t.Start)] }, chainDepth + 1));
            }
            else if (change.Kind == "cancel_animation") copy.Cancel(a => a.Animation.Id == change.AnimationId, "requested", chainDepth);
            else if (change.Kind == "geometry") copy.Cancel(a => a.Animation.Tracks.Any(t => t.NodeId == change.NodeId && ScreenAnimation.Touches(change.Patch!, t.Property)), "property_set", chainDepth);
            else if (change.Kind == "parent") copy.Cancel(_ => true, "reparented", chainDepth);
            copy.Cancel(a => a.Animation.Tracks.Any(t => !current.Nodes.Any(n => n.Id == t.NodeId)), "removed", chainDepth);
        }
        if (copy.active.Count > 16 || copy.active.Sum(a => a.Animation.Tracks.Length) > 256 || copy.Reserved > 128)
            throw new InvalidOperationException("Превышен бюджет живых анимаций.");
        return copy;
    }
    private static void CheckDepth(int depth)
    {
        if (depth > NovellMaker.Core.Triggers.AuthorTrigger.MaximumChainDepth)
            throw new InvalidOperationException("Превышена глубина цепочки результатов анимаций.");
    }
    private void Cancel(Func<Running, bool> predicate, string reason, int chainDepth)
    {
        foreach (var group in active.Where(predicate).ToArray())
        {
            var depth = Math.Max(group.ChainDepth, chainDepth + 1);
            CheckDepth(depth);
            active.Remove(group); results.Enqueue(new(group.Animation.Id, "cancelled", reason) { ChainDepth = depth });
        }
    }
    internal ScreenDefinition Tick(ScreenDefinition definition, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        foreach (var group in active.ToArray())
        {
            try
            {
                Advance(group, ref definition, seconds);
                if (group.Time >= group.Animation.Duration)
                { active.Remove(group); results.Enqueue(new(group.Animation.Id, "completed", "finished") { ChainDepth = group.ChainDepth }); }
            }
            catch (Exception error) when (error is ArgumentException or KeyNotFoundException)
            { active.Remove(group); results.Enqueue(new(group.Animation.Id, "failed", "invalid_geometry") { ChainDepth = group.ChainDepth }); }
        }
        return definition;
    }
    private static void Advance(Running group, ref ScreenDefinition definition, double seconds)
    {
        var end = Math.Min(group.Animation.Duration, group.Time + seconds);
        // The same coherent frames validate a plan and play it; declaration order never creates extra states.
        var boundaries = group.Animation.Tracks.SelectMany(t => new[] { t.Start, t.Start + t.Seconds })
            .Where(t => t >= group.Time && t <= end).Append(end).Distinct().Order().ToArray();
        foreach (var time in boundaries)
        {
            var nodes = definition.Nodes.ToDictionary(n => n.Id);
            for (var i = 0; i < group.Animation.Tracks.Length; i++)
            {
                var t = group.Animation.Tracks[i];
                if (group.From[i] is not { } from || time < t.Start) continue;
                var amount = Math.Clamp((time - t.Start) / t.Seconds, 0, 1);
                nodes[t.NodeId] = ScreenAnimation.Write(nodes[t.NodeId], t.Property, amount == 1 ? t.To : from + (t.To - from) * ScreenAnimation.Ease(t.Easing, amount));
            }
            // Finish preceding tracks before capturing the next track at the same boundary.
            for (var i = 0; i < group.Animation.Tracks.Length; i++)
            {
                var t = group.Animation.Tracks[i];
                if (group.From[i] is null && time >= t.Start) group.From[i] = ScreenAnimation.Read(nodes[t.NodeId], t.Property);
            }
            definition = new(definition.Id, [.. definition.Nodes.Select(n => nodes[n.Id])], definition.RetiredNodeIds);
            group.Time = time;
        }
    }
    internal IEnumerable<ScreenAnimationResult> Drain()
    { while (results.TryDequeue(out var result)) yield return result; }
}
