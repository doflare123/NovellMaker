using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly RuntimeScreens screens;
    private ScreenTextureCache<RenderedTexture>? componentTextures;
    private string lastMenuDiagnostics = "";
    internal ValueTask DisposeScreenLuaAsync() => screens.DisposeAsync();

    private bool RouteScreen(ScreenInput input)
    {
        var result = screens.Route(input, player.Execution.State.SceneId);
        ApplySceneNavigation();
        return result.Consumed;
    }

    private bool RouteProjectKey(string key)
    {
        screens.SyncScene(player.Execution.State.SceneId);
        var consumed = screens.RouteKey(key);
        ApplySceneNavigation();
        return consumed;
    }

    private void ApplySceneNavigation()
    {
        if (screens.NavigationRequested is { } scene)
        {
            screens.Reset(); player.Execution.IsPaused = false;
            player.EnterScene(scene); screens.SyncScene(player.Execution.State.SceneId);
            lineElapsed = TimeSpan.Zero; RefreshTextTextures(); RefreshInteractions();
        }
    }

    private void DrawComponents(SpriteBatch batch)
    {
        var instances = screens.Stack.Instances;
        componentTextures ??= new(raster => DialogueTextureRenderer.RenderButton(GraphicsDevice, raster.Text,
            raster.Width, raster.Height, raster.OffsetX, raster.OffsetY, raster.LogicalHeight, raster.Transparent));
        var textures = componentTextures.UpdateLayers(instances, new(0, 0, sceneTarget!.Width, sceneTarget.Height));
        if (pixel is null) return;
        var rendered = textures.ToDictionary(t => (t.Region.InstanceId, t.Region.Node.Id));
        foreach (var instance in instances)
        {
            if (!screens.IsSceneInstance(instance) && player.Execution.Project.Scenes.FirstOrDefault(s => s.Id == instance.Definition.Id)?.BackgroundImageId is { } background)
                DrawBackground(batch, player.Execution.Project.Images.FirstOrDefault(i => i.Id == background));
            foreach (var (node, bounds) in instance.Definition.VisibleRegions(new(0, 0, sceneTarget.Width, sceneTarget.Height)))
            {
                if (node.Kind == ScreenNodeKind.Panel)
                    batch.Draw(pixel, new Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height), new Color(20, 22, 26));
                if (node.ImageId is { } imageId && assetTextures.TryGetValue(imageId, out var sprite))
                {
                    var absolute = instance.Definition.AbsoluteBounds(node.Id);
                    var source = new Rectangle((int)((bounds.X - absolute.X) / absolute.Width * sprite.Width),
                        (int)((bounds.Y - absolute.Y) / absolute.Height * sprite.Height),
                        Math.Max(1, (int)(bounds.Width / absolute.Width * sprite.Width)), Math.Max(1, (int)(bounds.Height / absolute.Height * sprite.Height)));
                    batch.Draw(sprite, new Rectangle((int)bounds.X, (int)bounds.Y, Math.Max(1, (int)bounds.Width), Math.Max(1, (int)bounds.Height)), source, Color.White);
                }
                if (!rendered.TryGetValue((instance.Id, node.Id), out var item)) continue;
                var (region, texture) = item;
                var focused = screens.Stack.Focus is { } focus && focus.NodeId == node.Id && focus.InstanceId == instance.Id;
                var disabled = node.Kind == ScreenNodeKind.Button && !instance.Definition.Buttons.Any(n => n.Id == node.Id);
                batch.Draw(texture.Texture, new Vector2(region.X, region.Y),
                    disabled ? new Color(110, 115, 120) : focused ? new Color(160, 215, 255) : Color.White);
            }
        }
    }
}
