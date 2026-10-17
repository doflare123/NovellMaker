using System.Collections.Immutable;

namespace NovellMaker.Core.Scenes;

/// <summary>
/// An ordered collection of dialogue lines with optional presentation override.
/// </summary>
public sealed record NovelScene(Guid Id, string Name)
{
    public Guid Id { get; init; } = ValidateId(Id);

    public string Name { get; init; } = ValidateName(Name);

    public ImmutableArray<DialogueLine> Lines { get; init; } = [];

    public string? DialogueCssOverride { get; init; }

    public Guid? BackgroundImageId { get; init; }

    public static NovelScene Create(string name) => new(Guid.NewGuid(), name);

    public bool ContentEquals(NovelScene? other)
    {
        if (other is null ||
            Id != other.Id ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
            !string.Equals(DialogueCssOverride, other.DialogueCssOverride, StringComparison.Ordinal) ||
            BackgroundImageId != other.BackgroundImageId ||
            Lines.IsDefault != other.Lines.IsDefault)
        {
            return false;
        }

        if (Lines.IsDefault)
        {
            return true;
        }

        if (Lines.Length != other.Lines.Length)
        {
            return false;
        }

        for (var lineIndex = 0; lineIndex < Lines.Length; lineIndex++)
        {
            if (Lines[lineIndex] is null || !Lines[lineIndex].ContentEquals(other.Lines[lineIndex]))
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
            throw new ArgumentException("Идентификатор сцены не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Название сцены не может быть пустым.", nameof(name));
        }

        return name;
    }
}
