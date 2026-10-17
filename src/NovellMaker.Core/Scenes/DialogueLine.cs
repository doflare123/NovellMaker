using System.Collections.Immutable;

namespace NovellMaker.Core.Scenes;

/// <summary>
/// A localized dialogue entry addressed by stable identifiers.
/// </summary>
public sealed record DialogueLine(Guid Id, string TextKey)
{
    public Guid Id { get; init; } = ValidateId(Id);

    public string TextKey { get; init; } = ValidateTextKey(TextKey);

    public ImmutableDictionary<string, string> Texts { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    public static DialogueLine Create(string language, string text = "")
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("Код языка реплики не может быть пустым.", nameof(language));
        }

        ArgumentNullException.ThrowIfNull(text);

        var id = Guid.NewGuid();
        return new DialogueLine(id, $"line.{id:N}")
        {
            Texts = ImmutableDictionary<string, string>.Empty.Add(language, text),
        };
    }

    public bool ContentEquals(DialogueLine? other)
    {
        if (other is null ||
            Id != other.Id ||
            !string.Equals(TextKey, other.TextKey, StringComparison.Ordinal) ||
            Texts is null ||
            other.Texts is null ||
            Texts.Count != other.Texts.Count)
        {
            return false;
        }

        foreach (var (language, text) in Texts)
        {
            if (!other.Texts.TryGetValue(language, out var otherText) ||
                !string.Equals(text, otherText, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Guid ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор реплики не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateTextKey(string? textKey)
    {
        if (string.IsNullOrWhiteSpace(textKey))
        {
            throw new ArgumentException("Текстовый ключ реплики не может быть пустым.", nameof(textKey));
        }

        return textKey;
    }
}
