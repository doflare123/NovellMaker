namespace NovellMaker.Core.Assets;

public sealed record ImageAsset(
    Guid Id,
    string Name,
    string RelativePath,
    int PixelWidth,
    int PixelHeight)
{
    public const int MaximumDimension = 8192;
    public const long MaximumPixelCount = 32_000_000;

    public Guid Id { get; init; } = ValidateId(Id);

    public string Name { get; init; } = ValidateName(Name);

    public string RelativePath { get; init; } = ValidatePath(RelativePath);

    public int PixelWidth { get; init; } = ValidateDimension(PixelWidth, nameof(PixelWidth));

    public int PixelHeight { get; init; } = ValidateHeight(PixelWidth, PixelHeight);

    public static void ValidateForPersistence(ImageAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateId(asset.Id);
        ValidateName(asset.Name);
        ValidatePath(asset.RelativePath);
        ValidateDimension(asset.PixelWidth, nameof(asset.PixelWidth));
        ValidateDimension(asset.PixelHeight, nameof(asset.PixelHeight));

        if ((long)asset.PixelWidth * asset.PixelHeight > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(asset),
                $"Изображение не может содержать более {MaximumPixelCount:N0} пикселей.");
        }
    }

    private static Guid ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор изображения не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Название изображения не может быть пустым.", nameof(name));
        }

        return name;
    }

    private static string ValidatePath(string? path)
    {
        AssetPath.ValidateRelativePath(path!);
        return path!;
    }

    private static int ValidateDimension(int dimension, string parameterName)
    {
        if (dimension is < 1 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                dimension,
                $"Размер изображения должен быть от 1 до {MaximumDimension} пикселей.");
        }

        return dimension;
    }

    private static int ValidateHeight(int width, int height)
    {
        ValidateDimension(height, nameof(PixelHeight));
        if ((long)width * height > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                $"Изображение не может содержать более {MaximumPixelCount:N0} пикселей.");
        }

        return height;
    }
}
