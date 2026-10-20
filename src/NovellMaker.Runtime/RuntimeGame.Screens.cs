using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly RuntimeScreens screens = new(Guid.NewGuid(), enableLua: true);
    private readonly List<(ScreenNode Node, RenderedTexture Texture)> pauseTextures = [];
    private ScreenDefinition? pauseDefinition;
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
        if (instance is null || !instance.Definition.Nodes[0].Visible || pixel is null) return;
        if (!ReferenceEquals(pauseDefinition, instance.Definition))
        {
            foreach (var texture in pauseTextures) texture.Texture.Dispose();
            pauseTextures.Clear();
            pauseDefinition = instance.Definition;
            foreach (var node in instance.Definition.Nodes.Where(n => n.Visible && n.Kind is ScreenNodeKind.Button or ScreenNodeKind.Text))
                pauseTextures.Add((node, DialogueTextureRenderer.RenderButton(GraphicsDevice, node.Text,
                    (int)node.Bounds.Width, (int)node.Bounds.Height)));
        }
        var bounds = RuntimeScreens.PauseBounds;
        batch.Draw(pixel, new Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height), new Color(20, 22, 26));
        foreach (var (node, texture) in pauseTextures)
        {
            var focused = screens.Stack.Focus?.NodeId == node.Id;
            var disabled = node.Kind == ScreenNodeKind.Button && !instance.Definition.Buttons.Any(n => n.Id == node.Id);
            batch.Draw(texture.Texture, new Vector2((float)(bounds.X + node.Bounds.X), (float)(bounds.Y + node.Bounds.Y)),
                disabled ? new Color(110, 115, 120) : focused ? new Color(160, 215, 255) : Color.White);
        }
    }
}
