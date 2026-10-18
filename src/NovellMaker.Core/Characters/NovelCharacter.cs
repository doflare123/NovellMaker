using System.Collections.Immutable;

namespace NovellMaker.Core.Characters;

/// <summary>
/// A character with stable identity and named visual states.
/// </summary>
public sealed record NovelCharacter(Guid Id, string Name)
{
    public Guid Id { get; init; } = ValidateId(Id);

    public string Name { get; init; } = EnsureNameNotNull(Name);

    public ImmutableArray<CharacterState> States { get; init; } = [];

    public Guid? DefaultStateId { get; init; }

    public static NovelCharacter Create(string name) => new(Guid.NewGuid(), name);

    internal static void ValidateForPersistence(NovelCharacter character)
    {
        ValidateId(character.Id);
        ValidateName(character.Name);

        if (character.States.IsDefault)
        {
            throw new ArgumentException(
                $"Коллекция состояний персонажа «{character.Name}» не инициализирована.",
                nameof(character));
        }

        if (character.DefaultStateId == Guid.Empty)
        {
            throw new ArgumentException(
                $"Ссылка персонажа «{character.Name}» на состояние по умолчанию не может быть пустым GUID.",
                nameof(character));
        }

        foreach (var state in character.States)
        {
            if (state is null)
            {
                throw new ArgumentException(
                    $"Коллекция состояний персонажа «{character.Name}» содержит null.",
                    nameof(character));
            }

            CharacterState.ValidateForPersistence(state);
        }
    }

    public bool ContentEquals(NovelCharacter? other)
    {
        if (other is null ||
            Id != other.Id ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
            DefaultStateId != other.DefaultStateId ||
            States.IsDefault != other.States.IsDefault)
        {
            return false;
        }

        if (States.IsDefault)
        {
            return true;
        }

        if (States.Length != other.States.Length)
        {
            return false;
        }

        for (var stateIndex = 0; stateIndex < States.Length; stateIndex++)
        {
            var left = States[stateIndex];
            var right = other.States[stateIndex];
            if (left is null ||
                right is null ||
                left.Id != right.Id ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                left.ImageId != right.ImageId)
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
            throw new ArgumentException("Идентификатор персонажа не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Имя персонажа не может быть пустым.", nameof(name));
        }

        return name;
    }

    private static string EnsureNameNotNull(string? name) =>
        name ?? throw new ArgumentNullException(nameof(name));
}
