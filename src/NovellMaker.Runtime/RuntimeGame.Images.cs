using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Assets;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly Dictionary<Guid, Texture2D> assetTextures = [];
    private RuntimeImageCache<Texture2D>? imageCache;
    private RuntimeImagePreloader? imagePreloader;
    private (Guid Scene, Guid? Line, Guid? Choice, string Screens)? imageHintsKey;
    private ImageAsset[] imageHints = [];
    private bool hasPresentedFrame;
    private RenderedTexture? imageLoadingTexture;
    private int pendingImageDrawFrames;
    private int heldImageDrawFrames;
    private bool heldImageScreenshotWritten;
    private readonly HashSet<Guid> screenBudgetRejected = [];
    private readonly Dictionary<Guid, string> assetDiagnostics = [];

    private void RefreshFrameResources(bool poll = false)
    {
        if (imageCache is null) return;
        var wanted = new Dictionary<Guid, ImageAsset>();
        if (player.Frame.Background is { } background)
        {
            wanted[background.Id] = background;
        }

        foreach (var character in player.Frame.Characters)
        {
            wanted[character.Image.Id] = character.Image;
        }

        assetDiagnostics.Clear();
        screenBudgetRejected.Clear();
        var screenPixels = 0L; var screenImages = 0;
        foreach (var id in screens.Stack.Instances.SelectMany(i => i.Definition.VisibleRegions(new(0, 0, LogicalWidth, LogicalHeight)))
            .Where(r => r.Node.ImageId is not null).Select(r => r.Node.ImageId!.Value)
            .Concat(screens.Stack.Instances.Select(i => player.Execution.Project.Scenes.FirstOrDefault(s => s.Id == i.Definition.Id)?.BackgroundImageId)
                .OfType<Guid>()).Distinct())
        {
            if (wanted.ContainsKey(id) || player.Execution.Project.Images.FirstOrDefault(i => i.Id == id) is not { } image) continue;
            wanted[id] = image;
            var pixels = (long)image.PixelWidth * image.PixelHeight;
            if (screenImages >= 32 || screenPixels + pixels > 16 * 1024 * 1024)
            {
                screenBudgetRejected.Add(id);
                assetDiagnostics[id] = $"Изображение «{image.Name}»: превышен бюджет спрайтов экранов (32 изображения / 16 Мп).";
            }
            else { screenImages++; screenPixels += pixels; }
        }

        var state = player.Execution.State;
        var hintKey = (state.SceneId, state.LineId, state.Choice?.Id, string.Join(',', screens.Stack.Instances.Select(i => i.Definition.Id)));
        if (imageHintsKey != hintKey)
        {
            imageHintsKey = hintKey;
            imageHints = imagePreloader?.Hints(state, player.Frame, screens.Stack.Instances.Select(i => i.Definition.Id)) ?? [];
        }
        imageCache.SetWanted(wanted.Values.Where(asset => !screenBudgetRejected.Contains(asset.Id)), imageHints);
        if (poll) imageCache.Poll();
        assetTextures.Clear();
        foreach (var (id, asset) in wanted)
        {
            if (screenBudgetRejected.Contains(id)) continue;
            if (imageCache.Get(id) is { } texture) assetTextures[id] = texture;
            if (imageCache.Diagnostic(id) is { } message) assetDiagnostics[id] = $"Ресурс «{asset.Name}»: {message}";
        }
    }

}
