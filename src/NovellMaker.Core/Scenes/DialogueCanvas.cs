using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Scenes;

/// <summary>Adapter from scenario text to ordinary canvas nodes. It has no execution side effects.</summary>
public static class DialogueCanvas
{
    public static ImmutableArray<ScreenNode> Add(ImmutableArray<ScreenNode> nodes, Guid sceneId, DialogueStyle style, bool legacy = false, bool deterministic = false)
    {
        var root = nodes.Single(n => n.ParentId is null);
        Guid Id(string part) => deterministic
            ? new(SHA256.HashData(Encoding.UTF8.GetBytes($"NovellMaker/dialogue/{sceneId:D}/{part}"))[..16]) : Guid.NewGuid();
        // The old dialogue lived in viewport space below all canvas components. Preserve that
        // relationship even when an old canvas root is transformed, clipped, tinted or hidden.
        if (deterministic && (nodes.Length > 1 || root.Kind != ScreenNodeKind.Container || root.Bounds != ScreenDefinition.CanvasViewport ||
            root.Geometry != new ScreenGeometry() || root.Color != "#FFFFFF" || !root.Visible || !root.Enabled))
        {
            var wrapper = new ScreenNode(Id("root"), null, ScreenNodeKind.Container, ScreenDefinition.CanvasViewport);
            nodes = nodes.Replace(root, root with { ParentId = wrapper.Id, Order = 1000 }).Insert(0, wrapper);
            root = wrapper;
        }
        var panel = Id("panel");
        var width = Math.Clamp(Math.Ceiling(style.Width), 64, 1280);
        var height = Math.Clamp(style.MinHeight, 48, 720);
        return nodes.AddRange((ImmutableArray<ScreenNode>)[
            new(panel, root.Id, ScreenNodeKind.Panel, new(style.Left, 720 - style.Bottom - height, width, height))
            { Name = "Диалог", Dialogue = legacy ? DialogueBinding.CssPanel : DialogueBinding.Panel, Order = deterministic ? 0 : 1000 },
            new(Id("speaker"), panel, ScreenNodeKind.Text, new(style.PaddingLeft, style.PaddingTop, Math.Max(1, width - style.PaddingLeft - style.PaddingRight), Math.Max(1, style.FontSize * 1.025 + 6)))
            { Name = "Имя говорящего", Dialogue = DialogueBinding.Speaker },
            new(Id("text"), panel, ScreenNodeKind.Text, new(style.PaddingLeft, style.PaddingTop + style.FontSize * 1.025 + 6, Math.Max(1, width - style.PaddingLeft - style.PaddingRight), Math.Max(1, height - style.PaddingTop - style.PaddingBottom - style.FontSize * 1.025 - 6)))
            { Name = "Текст реплики", Dialogue = DialogueBinding.Text }
        ]);
    }

    // Metrics come from the same font/layout implementation in editor and runtime.
    public static ScreenDefinition Present(ScreenDefinition source, DialogueStyle style, string speaker, string text,
        bool hasLine, double legacyHeight, double speakerHeight, TimeSpan elapsed = default, bool animate = false)
    {
        if (!source.Nodes.Any(n => n.Dialogue != DialogueBinding.None)) return source;
        var panels = source.Nodes.Where(n => n.Dialogue == DialogueBinding.CssPanel).ToDictionary(n => n.Id);
        var sample = animate ? style.Sample(elapsed) : DialogueAnimationSample.Default;
        var nodes = source.Nodes.Select(node =>
        {
            if (node.Dialogue == DialogueBinding.None) return node;
            var bounds = node.Bounds;
            if (node.Dialogue == DialogueBinding.CssPanel)
                bounds = new(style.Left, 720 - style.Bottom - legacyHeight, Math.Clamp(Math.Ceiling(style.Width), 64, 1280), legacyHeight);
            else if (node.Dialogue is DialogueBinding.Text or DialogueBinding.Speaker && node.ParentId is { } parent && panels.ContainsKey(parent))
            {
                var width = Math.Max(1, Math.Clamp(Math.Ceiling(style.Width), 64, 1280) - style.PaddingLeft - style.PaddingRight);
                bounds = node.Dialogue == DialogueBinding.Speaker
                    ? new(style.PaddingLeft, style.PaddingTop, width, Math.Max(1, speakerHeight))
                    : new(style.PaddingLeft, style.PaddingTop + speakerHeight, width, Math.Max(1, legacyHeight - style.PaddingTop - style.PaddingBottom - speakerHeight));
            }
            var geometry = node.Geometry;
            if (node.Dialogue is DialogueBinding.Panel or DialogueBinding.CssPanel)
            {
                // Compose the CSS center-scale with the authored pivot/rotation/scale.
                var matrix = geometry.Matrix(bounds, 0, 0);
                var cx = bounds.Width * (.5 - geometry.PivotX); var cy = bounds.Height * (.5 - geometry.PivotY);
                bounds = bounds with { X = bounds.X + sample.TranslateX + (1 - sample.Scale) * (matrix.A * cx + matrix.C * cy),
                    Y = bounds.Y + sample.TranslateY + (1 - sample.Scale) * (matrix.B * cx + matrix.D * cy) };
                static double Scale(double value) => Math.CopySign(Math.Clamp(Math.Abs(value), .001, 1000), value);
                geometry = geometry with { ScaleX = Scale(geometry.ScaleX * sample.Scale), ScaleY = Scale(geometry.ScaleY * sample.Scale), Opacity = geometry.Opacity * sample.Opacity };
            }
            return node with { Bounds = bounds, Geometry = geometry,
                Visible = node.Visible && hasLine && (node.Dialogue is not (DialogueBinding.Panel or DialogueBinding.CssPanel) || sample.Scale > 0) && (node.Dialogue != DialogueBinding.Speaker || !string.IsNullOrWhiteSpace(speaker)),
                Text = node.Dialogue == DialogueBinding.Text ? text : node.Dialogue == DialogueBinding.Speaker ? speaker : node.Text };
        }).ToImmutableArray();
        return new(source.Id, nodes, source.RetiredNodeIds, presentation: true);
    }

    /// <summary>Freeze CSS auto layout before direct manipulation; keep all IDs, bindings and authored transforms.</summary>
    public static ImmutableArray<ScreenNode> Materialize(ScreenDefinition source, ScreenDefinition presentation)
    {
        var panels = source.Nodes.Where(n => n.Dialogue == DialogueBinding.CssPanel).Select(n => n.Id).ToHashSet();
        return [.. source.Nodes.Select(n => n.Dialogue == DialogueBinding.CssPanel
            ? n with { Dialogue = DialogueBinding.Panel, Bounds = presentation.Nodes.Single(p => p.Id == n.Id).Bounds }
            : n.Dialogue is DialogueBinding.Text or DialogueBinding.Speaker && n.ParentId is { } parent && panels.Contains(parent)
                ? n with { Bounds = presentation.Nodes.Single(p => p.Id == n.Id).Bounds } : n)];
    }
}
