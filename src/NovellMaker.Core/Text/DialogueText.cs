using System.Text;

namespace NovellMaker.Core.Text;

public sealed record TextRun(string Text, string? Color);

/// <summary>Color-only inline syntax. Unsupported sequences remain literal text.</summary>
public static class DialogueText
{
    public static IReadOnlyList<TextRun> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runs = new List<TextRun>();
        var buffer = new StringBuilder();
        string? color = null;
        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new(buffer.ToString(), color));
            buffer.Clear();
        }
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '§' || index + 1 >= text.Length) { buffer.Append(text[index]); continue; }
            var code = char.ToLowerInvariant(text[index + 1]);
            if (code == '§') { buffer.Append('§'); index++; continue; }
            if (code == 'r') { Flush(); color = null; index++; continue; }
            if (code == '#' && index + 7 < text.Length && text.AsSpan(index + 2, 6).ToArray().All(char.IsAsciiHexDigit))
            {
                Flush();
                color = text.Substring(index + 1, 7).ToUpperInvariant();
                index += 7;
                continue;
            }
            buffer.Append('§');
        }
        Flush();
        return runs;
    }
}
