using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NovellMaker.Core.Execution;

namespace NovellMaker.Editor.Views;

/// <summary>Displays Core's sampled parts. Owns only bitmap views, never scenario execution.</summary>
public sealed class CharacterVisualView : UserControl
{
    private readonly Canvas canvas = new() { Background = Brushes.Transparent };
    private readonly Viewbox viewport;
    private readonly List<ProjectImageView> parts = [];
    public CharacterVisualView()
    {
        viewport = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, ClipToBounds = false };
        Content = viewport;
    }

    public void Present(VisibleCharacter character, string? directory, int revision)
    {
        canvas.Width = character.Width; canvas.Height = character.Height;
        var sprites = character.Sprites;
        while (parts.Count > sprites.Length)
        { canvas.Children.Remove(parts[^1]); parts.RemoveAt(parts.Count - 1); }
        for (var index = 0; index < sprites.Length; index++)
        {
            if (parts.Count <= index)
            {
                var added = new ProjectImageView { TransparentBackground = true, IsHitTestVisible = false,
                    ImageStretch = Stretch.Fill, RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute), MaximumWidth = 1280 };
                parts.Add(added); canvas.Children.Add(added);
            }
            var sprite = sprites[index];
            var image = character.Assets.FirstOrDefault(i => i.Id == sprite.ImageId) ?? (sprite.ImageId == character.Image.Id ? character.Image : null);
            var view = parts[index];
            view.Asset = image; view.ProjectDirectory = directory; view.Revision = revision;
            view.Width = image?.PixelWidth ?? 1; view.Height = image?.PixelHeight ?? 1;
            view.Opacity = sprite.Opacity; view.ZIndex = index;
            var m = sprite.World;
            if (character.Visual?.Clips.FirstOrDefault(c => c.Id == character.Visual.DefaultClipId) is { Frames.IsEmpty: false })
                m = System.Numerics.Matrix3x2.CreateScale(character.Width / (float)view.Width, character.Height / (float)view.Height);
            view.RenderTransform = new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32));
        }
    }
}
