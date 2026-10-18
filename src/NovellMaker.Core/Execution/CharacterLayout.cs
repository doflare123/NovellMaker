using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Execution;

public readonly record struct SpriteBounds(double X, double Y, double Width, double Height);

public static class CharacterLayout
{
    public const double Width = 1280;
    public const double Height = 720;

    public static SpriteBounds Calculate(int pixelWidth, int pixelHeight, int index, int count, CharacterPlacement? placement)
    {
        var slot = 1200d / Math.Max(1, count);
        var scale = Math.Min((placement is null ? Math.Min(360, slot) : 360) / pixelWidth, 560d / pixelHeight)
            * (placement?.Scale ?? 1);
        var width = pixelWidth * scale;
        var height = pixelHeight * scale;
        var x = placement is null ? 40 + slot * (index + 0.5) : placement.X * Width;
        return new(x - width / 2, (placement?.Y ?? 1) * Height - height, width, height);
    }
}
