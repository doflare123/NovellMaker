using System.Globalization;
using System.Text;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;
using NovellMaker.Core.Text;
using SkiaSharp;

namespace NovellMaker.Rendering;

// Compiled into Editor and Runtime: identical fonts, wrapping, inline colors and panel painting.
internal static class DialogueRasterizer
{
    internal const int MaximumLayoutCharacters = 8192;
    internal static string LayoutText(string text) => text.Length <= MaximumLayoutCharacters
        ? text : "[Текст превышает лимит отображения: 8192 символа]";
    private static readonly object measurementLock = new();
    private static (DialogueStyle Style, string Speaker, string Text)? measurementKey;
    private static (double Height, double SpeakerHeight) measurement;

    internal static (double Height, double SpeakerHeight) Measure(DialogueStyle style, string speaker, string text)
    {
        speaker = LayoutText(speaker); text = LayoutText(text);
        lock (measurementLock)
        {
            var key = (style, speaker, text);
            if (measurementKey == key) return measurement;
            measurement = MeasureCore(style, speaker, text);
            measurementKey = key;
            return measurement;
        }
    }

    private static (double Height, double SpeakerHeight) MeasureCore(DialogueStyle style, string speaker, string text)
    {
        var width = Math.Clamp((int)Math.Ceiling(style.Width), 64, 1280);
        using var typeface = CreateTypeface(style.FontFamily);
        using var font = new SKFont(typeface, (float)style.FontSize);
        using var nameFont = new SKFont(typeface, Math.Max(12, (float)style.FontSize * .82f)) { Embolden = true };
        using var paint = new SKPaint { IsAntialias = true };
        var contentWidth = Math.Max(1, width - (float)Math.Clamp(style.PaddingLeft + style.PaddingRight, 0, width - 1));
        var lines = LayoutRuns(DialogueText.Parse(text), font, paint, contentWidth);
        var speakerHeight = string.IsNullOrWhiteSpace(speaker) ? 0 : LineHeight(nameFont, 1.25f) + 6;
        return (Math.Clamp(Math.Ceiling(Math.Max(style.MinHeight, style.PaddingTop + speakerHeight + Math.Max(1, lines.Count) * LineHeight(font, 1.2f) + style.PaddingBottom)), 48, 720), speakerHeight);
    }

    internal static SKBitmap Render(DialogueBinding binding, DialogueStyle style, string text,
        double logicalWidth, double logicalHeight, int width, int height, int offsetX = 0, int offsetY = 0)
    {
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048 || (long)width * height > 262144)
            throw new ArgumentOutOfRangeException(nameof(width), "Превышен бюджет текстуры диалога.");
        text = LayoutText(text);
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent); canvas.Translate(-offsetX, -offsetY);
            if (binding is DialogueBinding.Panel or DialogueBinding.CssPanel)
            {
                var radius = (float)Math.Min(style.BorderRadius, Math.Min(logicalWidth, logicalHeight) / 2);
                using var fill = new SKPaint { IsAntialias = true, Color = ParseColor(style.BackgroundColor) };
                canvas.DrawRoundRect(new SKRect(0, 0, (float)logicalWidth, (float)logicalHeight), radius, radius, fill);
                if (style.BorderWidth > 0)
                {
                    var inset = (float)Math.Min(style.BorderWidth / 2, Math.Min(logicalWidth, logicalHeight) / 2);
                    using var border = new SKPaint { IsAntialias = true, Color = ParseColor(style.BorderColor), Style = SKPaintStyle.Stroke, StrokeWidth = (float)style.BorderWidth };
                    canvas.DrawRoundRect(new SKRect(inset, inset, (float)logicalWidth - inset, (float)logicalHeight - inset), Math.Max(0, radius - inset), Math.Max(0, radius - inset), border);
                }
                return bitmap;
            }
            using var typeface = CreateTypeface(style.FontFamily);
            using var font = new SKFont(typeface, binding == DialogueBinding.Speaker ? Math.Max(12, (float)style.FontSize * .82f) : (float)style.FontSize)
            { Embolden = binding == DialogueBinding.Speaker };
            using var paint = new SKPaint { IsAntialias = true, Color = ParseColor(style.TextColor) };
            var runs = binding == DialogueBinding.Speaker ? new[] { new TextRun(text, null) } : DialogueText.Parse(text);
            var lines = LayoutRuns(runs, font, paint, (float)logicalWidth);
            var baseline = -font.Metrics.Ascent;
            var lineHeight = LineHeight(font, binding == DialogueBinding.Speaker ? 1.25f : 1.2f);
            foreach (var line in lines)
            {
                var x = binding == DialogueBinding.Speaker ? 0 : style.TextAlign switch
                { "center" => ((float)logicalWidth - line.Width) / 2, "right" => (float)logicalWidth - line.Width, _ => 0 };
                if (baseline + font.Metrics.Descent >= offsetY && baseline + font.Metrics.Ascent <= offsetY + height)
                    foreach (var piece in line.Pieces)
                    {
                        paint.Color = piece.Color is null ? ParseColor(style.TextColor) : ParseInlineColor(piece.Color, ParseColor(style.TextColor));
                        canvas.DrawText(piece.Text, x, baseline, font, paint); x += piece.Width;
                    }
                baseline += lineHeight;
                if (baseline + font.Metrics.Ascent > offsetY + height) break;
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    private static float LineHeight(SKFont font, float factor) => Math.Max(font.Size * factor, font.Metrics.Descent - font.Metrics.Ascent + font.Metrics.Leading);
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


