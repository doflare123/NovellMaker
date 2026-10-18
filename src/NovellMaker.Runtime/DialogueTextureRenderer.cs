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
    private const int LogicalWidth = 1280;
    private const int LogicalHeight = 720;

    public static RenderedTexture RenderDialogue(
        GraphicsDevice graphicsDevice,
        DialogueStyle style,
        string speakerName,
        string text)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(speakerName);
        ArgumentNullException.ThrowIfNull(text);

        var width = Math.Clamp((int)Math.Ceiling(style.Width), 64, LogicalWidth);
        var fontSize = (float)Math.Clamp(style.FontSize, 8, 160);
        using var typeface = CreateTypeface(style.FontFamily);
        using var bodyFont = new SKFont(typeface, fontSize);
        using var nameFont = new SKFont(typeface, Math.Max(12, fontSize * 0.82f)) { Embolden = true };
        using var measurePaint = new SKPaint { IsAntialias = true };

        var contentWidth = Math.Max(
            1,
            width - (float)Math.Clamp(style.PaddingLeft + style.PaddingRight, 0, width - 1));
        var lines = LayoutRuns(DialogueText.Parse(text), bodyFont, measurePaint, contentWidth);
        var bodyMetrics = bodyFont.Metrics;
        var bodyLineHeight = Math.Max(fontSize * 1.2f, bodyMetrics.Descent - bodyMetrics.Ascent + bodyMetrics.Leading);
        var nameMetrics = nameFont.Metrics;
        var nameLineHeight = string.IsNullOrWhiteSpace(speakerName)
            ? 0
            : Math.Max(nameFont.Size * 1.25f, nameMetrics.Descent - nameMetrics.Ascent + nameMetrics.Leading) + 6;
        var contentHeight = (float)style.PaddingTop + nameLineHeight +
            Math.Max(bodyLineHeight, lines.Count * bodyLineHeight) + (float)style.PaddingBottom;
        var height = Math.Clamp(
            (int)Math.Ceiling(Math.Max(style.MinHeight, contentHeight)),
            48,
            LogicalHeight);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var rect = new SKRect(0, 0, width, height);
        var radius = (float)Math.Max(0, style.BorderRadius);
        using (var backgroundPaint = new SKPaint
        {
            Color = ParseColor(style.BackgroundColor),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        })
        {
            canvas.DrawRoundRect(rect, radius, radius, backgroundPaint);
        }

        if (style.BorderWidth > 0)
        {
            var inset = (float)Math.Min(style.BorderWidth / 2, Math.Min(width, height) / 2d);
            using var borderPaint = new SKPaint
            {
                Color = ParseColor(style.BorderColor),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)style.BorderWidth,
            };
            canvas.DrawRoundRect(
                new SKRect(inset, inset, width - inset, height - inset),
                Math.Max(0, radius - inset),
                Math.Max(0, radius - inset),
                borderPaint);
        }

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, radius), SKClipOperation.Intersect, antialias: true);
        var defaultColor = ParseColor(style.TextColor);
        var xStart = (float)style.PaddingLeft;
        var baseline = (float)style.PaddingTop;
        using var textPaint = new SKPaint { IsAntialias = true, Color = defaultColor };

        if (!string.IsNullOrWhiteSpace(speakerName))
        {
            baseline -= nameMetrics.Ascent;
            canvas.DrawText(speakerName, xStart, baseline, nameFont, textPaint);
            baseline += nameMetrics.Descent + nameMetrics.Leading + 6;
        }

        baseline -= bodyMetrics.Ascent;
        foreach (var line in lines)
        {
            var lineX = style.TextAlign switch
            {
                "center" => xStart + ((contentWidth - line.Width) / 2),
                "right" => xStart + contentWidth - line.Width,
                _ => xStart,
            };

            foreach (var piece in line.Pieces)
            {
                textPaint.Color = piece.Color is null ? defaultColor : ParseInlineColor(piece.Color, defaultColor);
                canvas.DrawText(piece.Text, lineX, baseline, bodyFont, textPaint);
                lineX += piece.Width;
            }

            baseline += bodyLineHeight;
        }

        canvas.Restore();
        return new RenderedTexture(CreateTexture(graphicsDevice, bitmap), width, height);
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
