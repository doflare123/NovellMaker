using NovellMaker.Core.Characters;

namespace NovellMaker.Editor.Services;

public sealed record DialoguePasteLine(Guid? SpeakerId, string Text);
public sealed record ParsedDialogueLine(int Number, string Speaker, string Text);

public static class DialoguePaste
{
    public static IReadOnlyList<ParsedDialogueLine> Parse(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
        .Select((line, index) =>
        {
            var colon = line.IndexOf(':');
            return colon > 0 ? new ParsedDialogueLine(index + 1, line[..colon].Trim(), line[(colon + 1)..].Trim()) : new(index + 1, "", line.Trim());
        }).Where(line => line.Speaker.Length > 0 || line.Text.Length > 0).ToArray();

    // Duplicate character names deliberately require an explicit choice.
    public static Guid? Match(string name, IEnumerable<NovelCharacter> characters)
    {
        var matches = characters.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }
}
