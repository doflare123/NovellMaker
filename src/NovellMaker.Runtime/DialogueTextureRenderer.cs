using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Styling;
using NovellMaker.Core.Text;
using SkiaSharp;

namespace NovellMaker.Runtime;

internal static class DialogueTextureRenderer
{
    internal static RenderedTexture RenderComponent(GraphicsDevice device, ScreenRaster raster)
    {
        if (raster.Dialogue == NovellMaker.Core.Screens.DialogueBinding.None || raster.Style is null)
            return RenderButton(device, raster.Text, raster.Width, raster.Height, raster.OffsetX, raster.OffsetY, raster.LogicalHeight, raster.Transparent);
        using var bitmap = NovellMaker.Rendering.DialogueRasterizer.Render(raster.Dialogue, raster.Style, raster.Text,
            raster.LogicalWidth, raster.LogicalHeight, raster.Width, raster.Height, raster.OffsetX, raster.OffsetY);
        return new(CreateTexture(device, bitmap), raster.Width, raster.Height);
    }
    public static RenderedTexture? RenderDiagnostics(
        GraphicsDevice graphicsDevice,
        IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return null;
        }

        const int width = 1120;
        const float fontSize = 17;
        const float padding = 14;
        using var typeface = CreateTypeface("Segoe UI");
        using var font = new SKFont(typeface, fontSize);
        using var measurePaint = new SKPaint { IsAntialias = true };
        var runs = diagnostics.SelectMany(
            (message, index) => new[]
            {
                new TextRun(message, null),
                new TextRun(index == diagnostics.Count - 1 ? string.Empty : "\n", null),
            }).ToArray();
        var lines = LayoutRuns(runs, font, measurePaint, width - (padding * 2));
        var metrics = font.Metrics;
        var lineHeight = Math.Max(fontSize * 1.25f, metrics.Descent - metrics.Ascent + metrics.Leading);
        var height = Math.Clamp((int)Math.Ceiling((padding * 2) + lines.Count * lineHeight), 48, 240);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(36, 16, 18, 235));
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 204, 207) };
        var y = padding - metrics.Ascent;
        foreach (var line in lines)
        {
            var x = padding;
            foreach (var piece in line.Pieces)
            {
                canvas.DrawText(piece.Text, x, y, font, paint);
                x += piece.Width;
            }

            y += lineHeight;
            if (y > height)
            {
                break;
            }
        }

        return new RenderedTexture(CreateTexture(graphicsDevice, bitmap), width, height);
    }

    public static RenderedTexture RenderNavigationControls(
        GraphicsDevice graphicsDevice,
        bool canPrevious,
        bool canNext)
    {
        var width = RuntimeInputMapper.NextButtonBounds.Right - RuntimeInputMapper.BackButtonBounds.Left;
        var height = RuntimeInputMapper.BackButtonBounds.Height;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var typeface = CreateTypeface("Segoe UI");
        using var font = new SKFont(typeface, 16) { Embolden = true };
        DrawNavigationButton(canvas, font, new SKRect(0, 0, 140, 40), "Назад  ←", canPrevious);
        DrawNavigationButton(canvas, font, new SKRect(148, 0, 288, 40), "Вперёд  →", canNext);
        return new RenderedTexture(CreateTexture(graphicsDevice, bitmap), width, height);
    }

    public static RenderedTexture RenderButton(GraphicsDevice device, string text, int width, int height,
        int offsetX = 0, int offsetY = 0, double? logicalHeight = null, bool transparent = false)
    {
        if (width <= 0 || height <= 0 || width > ScreenTextureCache<RenderedTexture>.MaximumTextureSide ||
            height > ScreenTextureCache<RenderedTexture>.MaximumTextureSide ||
            (long)width * height > ScreenTextureCache<RenderedTexture>.MaximumTexturePixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Превышен бюджет текстуры кнопки.");
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? SKColors.Transparent : new SKColor(40, 45, 52, 245));
        using var typeface = CreateTypeface("Segoe UI");
        using var font = new SKFont(typeface, 18);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(230, 231, 233) };
        canvas.DrawText(text, 12 - offsetX, (float)((logicalHeight ?? height) / 2) - offsetY -
            (font.Metrics.Ascent + font.Metrics.Descent) / 2, font, paint);
        return new RenderedTexture(CreateTexture(device, bitmap), width, height);
    }

    private static List<TextLine> LayoutRuns(
        IReadOnlyList<TextRun> runs,
        SKFont font,
        SKPaint measurePaint,
        float maximumWidth)
    {
        var lines = new List<TextLine> { new() };
        foreach (var run in runs)
        {
            foreach (var token in Tokenize(run))
            {
                if (token.ForcedBreak)
                {
                    lines.Add(new TextLine());
                    continue;
                }

                AddToken(lines, token.Text, token.Color, font, measurePaint, maximumWidth);
            }
        }

        return lines;
    }

    private static void AddToken(
        List<TextLine> lines,
        string text,
        string? color,
        SKFont font,
        SKPaint measurePaint,
        float maximumWidth)
    {
        if (text.Length == 0)
        {
            return;
        }

        var width = font.MeasureText(text, measurePaint);
        var current = lines[^1];
        if (width <= maximumWidth)
        {
            if (current.Width > 0 && current.Width + width > maximumWidth)
            {
                current = new TextLine();
                lines.Add(current);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }
            }

            current.Add(text, color, width);
            return;
        }

        foreach (var element in EnumerateTextElements(text))
        {
            var elementWidth = font.MeasureText(element, measurePaint);
            current = lines[^1];
            if (current.Width > 0 && current.Width + elementWidth > maximumWidth)
            {
                current = new TextLine();
                lines.Add(current);
            }

            current.Add(element, color, elementWidth);
        }
    }

    private static IEnumerable<TextToken> Tokenize(TextRun run)
    {
        var buffer = new StringBuilder();
        bool? whitespace = null;
        foreach (var element in EnumerateTextElements(run.Text.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            if (element == "\n")
            {
                if (buffer.Length > 0)
                {
                    yield return new TextToken(buffer.ToString(), run.Color, ForcedBreak: false);
                    buffer.Clear();
                }

                whitespace = null;
                yield return new TextToken(string.Empty, run.Color, ForcedBreak: true);
                continue;
            }

            var isWhitespace = char.IsWhiteSpace(element, 0);
            if (whitespace.HasValue && whitespace.Value != isWhitespace && buffer.Length > 0)
            {
                yield return new TextToken(buffer.ToString(), run.Color, ForcedBreak: false);
                buffer.Clear();
            }

            whitespace = isWhitespace;
            buffer.Append(element);
        }

        if (buffer.Length > 0)
        {
            yield return new TextToken(buffer.ToString(), run.Color, ForcedBreak: false);
        }
    }

    private static IEnumerable<string> EnumerateTextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private static SKTypeface CreateTypeface(string family)
    {
        var requested = SKTypeface.FromFamilyName(family);
        if (requested is not null && requested.ContainsGlyph('Ж'))
        {
            return requested;
        }

        requested?.Dispose();
        return SKFontManager.Default.MatchCharacter(family, 'Ж') ??
            SKTypeface.FromFamilyName("sans-serif") ??
            throw new InvalidOperationException("Не найден системный шрифт для кириллицы.");
    }

    private static SKColor ParseInlineColor(string value, SKColor fallback) =>
        value.Length == 7 && value[0] == '#' && uint.TryParse(
            value.AsSpan(1),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var rgb)
            ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;

    private static void DrawNavigationButton(
        SKCanvas canvas,
        SKFont font,
        SKRect bounds,
        string label,
        bool enabled)
    {
        using var fill = new SKPaint
        {
            Color = enabled ? new SKColor(52, 57, 66, 245) : new SKColor(37, 40, 45, 225),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var border = new SKPaint
        {
            Color = enabled ? new SKColor(140, 149, 164) : new SKColor(77, 83, 92),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        using var text = new SKPaint
        {
            Color = enabled ? new SKColor(240, 242, 245) : new SKColor(128, 133, 141),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(bounds, 5, 5, fill);
        canvas.DrawRoundRect(new SKRect(bounds.Left + 0.5f, bounds.Top + 0.5f, bounds.Right - 0.5f, bounds.Bottom - 0.5f), 5, 5, border);
        var labelWidth = font.MeasureText(label, text);
        var metrics = font.Metrics;
        var x = bounds.MidX - (labelWidth / 2);
        var y = bounds.MidY - ((metrics.Ascent + metrics.Descent) / 2);
        canvas.DrawText(label, x, y, font, text);
    }

    private static SKColor ParseColor(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#' || !uint.TryParse(
            value.AsSpan(1),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var packed))
        {
            return SKColors.Magenta;
        }

        return value.Length == 7
            ? new SKColor((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : new SKColor((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    private static Texture2D CreateTexture(GraphicsDevice graphicsDevice, SKBitmap bitmap)
    {
        var bytes = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        var texture = new Texture2D(graphicsDevice, bitmap.Width, bitmap.Height, false, SurfaceFormat.Color);
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

    private sealed class TextLine
    {
        public List<TextPiece> Pieces { get; } = [];

        public float Width { get; private set; }

        public void Add(string text, string? color, float width)
        {
            if (Pieces.Count > 0 && string.Equals(Pieces[^1].Color, color, StringComparison.Ordinal))
            {
                var previous = Pieces[^1];
                Pieces[^1] = previous with { Text = previous.Text + text, Width = previous.Width + width };
            }
            else
            {
                Pieces.Add(new TextPiece(text, color, width));
            }

            Width += width;
        }
    }

    private sealed record TextPiece(string Text, string? Color, float Width);

    private readonly record struct TextToken(string Text, string? Color, bool ForcedBreak);
}

internal sealed record RenderedTexture(Texture2D Texture, int Width, int Height) : IDisposable
{
    public void Dispose() => Texture.Dispose();
}
