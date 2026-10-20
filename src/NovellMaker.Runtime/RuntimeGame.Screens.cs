using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly RuntimeScreens screens = new(Guid.NewGuid(), enableLua: true);
    private ScreenTextureCache<RenderedTexture>? pauseTextures;
    internal ValueTask DisposeScreenLuaAsync() => screens.DisposeAsync();

    private bool RouteScreen(ScreenInput input)
    {
        var previousMixer = showMixer;
        var result = screens.Route(input);
        if (result.Activation is { } activation && RuntimeScreens.MixerCommands.TryGetValue(activation.NodeId, out var command))
            ChangeMixer(command.Group, command.Change);
        // Screen topology can change in routing; update audio immediately without waiting a frame.
        if (previousMixer != showMixer) RefreshMixer();
        return result.Consumed;
    }

    private void DrawPause(SpriteBatch batch)
    {
        var instance = screens.Stack.Instances.FirstOrDefault(i => i.Definition.Id == RuntimeScreens.PauseId);
        pauseTextures ??= new(raster => DialogueTextureRenderer.RenderButton(GraphicsDevice, raster.Text,
            raster.Width, raster.Height, raster.OffsetX, raster.OffsetY, raster.LogicalHeight));
        var textures = pauseTextures.Update(instance?.Definition, new(0, 0, sceneTarget!.Width, sceneTarget.Height));
        if (instance is null || !instance.Definition.Nodes[0].Visible || pixel is null) return;
        var bounds = RuntimeScreens.PauseBounds;
        batch.Draw(pixel, new Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height), new Color(20, 22, 26));
        foreach (var (region, texture) in textures)
        {
            var node = region.Node;
            var focused = screens.Stack.Focus?.NodeId == node.Id;
            var disabled = node.Kind == ScreenNodeKind.Button && !instance.Definition.Buttons.Any(n => n.Id == node.Id);
            batch.Draw(texture.Texture, new Vector2(region.X, region.Y),
                disabled ? new Color(110, 115, 120) : focused ? new Color(160, 215, 255) : Color.White);
        }
    }
}
