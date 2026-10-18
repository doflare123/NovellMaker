namespace NovellMaker.Core.Characters;

/// <summary>
/// A named visual state of a character backed by an imported image asset.
/// </summary>
public sealed record CharacterState(Guid Id, string Name, Guid ImageId)
{
    public Guid Id { get; init; } = ValidateId(Id);

    public string Name { get; init; } = EnsureNameNotNull(Name);

    public Guid ImageId { get; init; } = ValidateImageId(ImageId);

    internal static void ValidateForPersistence(CharacterState state)
    {
        ValidateId(state.Id);
        ValidateName(state.Name);
        ValidateImageId(state.ImageId);
    }

    private static Guid ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор состояния персонажа не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Название состояния персонажа не может быть пустым.", nameof(name));
        }

        return name;
    }

    private static string EnsureNameNotNull(string? name) =>
        name ?? throw new ArgumentNullException(nameof(name));

    private static Guid ValidateImageId(Guid imageId)
    {
        if (imageId == Guid.Empty)
        {
            throw new ArgumentException("Ссылка состояния на изображение не может быть пустым GUID.", nameof(imageId));
        }

        return imageId;
    }
}
