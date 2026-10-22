using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;
using NovellMaker.Rendering;
using SkiaSharp;

namespace NovellMaker.Editor.Views;

/// <summary>Same rasterization as the game; only the visible local region is allocated.</summary>
internal sealed class DialogueComponentView : Control
{
    private readonly List<(Bitmap Image, Rect Bounds)> tiles = [];
    private (ScreenNode Node, DialogueStyle Style, ScreenRect Clip)? key;
    internal void Update(ScreenNode node, DialogueStyle style, ScreenRect clip)
    {
        var next = (node, style, clip);
        if (key == next) return;
        Clear(); key = next;
        var left = Math.Max(0, (int)Math.Floor(clip.X + 1e-8)); var top = Math.Max(0, (int)Math.Floor(clip.Y + 1e-8));
        var width = (int)Math.Ceiling(Math.Min(node.Bounds.Width, clip.X + clip.Width) - 1e-8) - left;
        var height = (int)Math.Ceiling(Math.Min(node.Bounds.Height, clip.Y + clip.Height) - 1e-8) - top;
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048) return;
        var strip = Math.Max(1, 262144 / width);
        for (var y = top; y < top + height; y += strip)
        {
            var h = Math.Min(strip, top + height - y);
            using var bitmap = DialogueRasterizer.Render(node.Dialogue, style, node.Text, node.Bounds.Width, node.Bounds.Height, width, h, left, y);
            if (node.Color != "#FFFFFF")
            {
                ScreenTint.TryParse(node.Color, out var tint);
                using var canvas = new SKCanvas(bitmap); using var paint = new SKPaint
                { Color = new SKColor((byte)(tint.R * 255), (byte)(tint.G * 255), (byte)(tint.B * 255)), BlendMode = SKBlendMode.Modulate };
                canvas.DrawRect(new SKRect(0, 0, width, h), paint);
            }
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); using var stream = data.AsStream();
            tiles.Add((new Bitmap(stream), new Rect(left, y, width, h)));
        }
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        foreach (var (image, bounds) in tiles) context.DrawImage(image, new Rect(image.Size), bounds);
    }
    private void Clear() { foreach (var tile in tiles) tile.Image.Dispose(); tiles.Clear(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Clear(); key = null; base.OnDetachedFromVisualTree(e); }
}
