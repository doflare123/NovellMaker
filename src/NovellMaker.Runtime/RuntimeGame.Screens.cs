using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly RuntimeScreens screens;
    private ScreenTextureCache<RenderedTexture>? componentTextures;
    private BasicEffect? componentEffect;
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
        batch.End();
        componentEffect ??= new BasicEffect(GraphicsDevice) { TextureEnabled = true, VertexColorEnabled = true,
            Projection = Matrix.CreateOrthographicOffCenter(0, sceneTarget.Width, sceneTarget.Height, 0, 0, 1) };
        static Color Mix(Color a, Color b) => new(a.ToVector4() * b.ToVector4());
        void DrawMesh(Texture2D texture, ScreenVisual visual, ScreenRect textureRect, Color color)
        {
            var vertices = ScreenMesh.Build(visual, textureRect, color);
            if (vertices.Length == 0) return;
            GraphicsDevice.BlendState = BlendState.AlphaBlend; GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone; GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
            componentEffect.Texture = texture;
            foreach (var pass in componentEffect.CurrentTechnique.Passes)
            { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3); }
        }
        foreach (var instance in instances)
        {
            if (!screens.IsSceneInstance(instance) && player.Execution.Project.Scenes.FirstOrDefault(s => s.Id == instance.Definition.Id)?.BackgroundImageId is { } background)
            {
                batch.Begin(); DrawBackground(batch, player.Execution.Project.Images.FirstOrDefault(i => i.Id == background)); batch.End();
            }
            foreach (var visual in instance.Definition.Visuals(new(0, 0, sceneTarget.Width, sceneTarget.Height)))
            {
                var node = visual.Node;
                var local = new ScreenRect(0, 0, node.Bounds.Width, node.Bounds.Height);
                var tint = new Color((float)(visual.Tint.R * visual.Opacity), (float)(visual.Tint.G * visual.Opacity), (float)(visual.Tint.B * visual.Opacity), (float)visual.Opacity);
                if (node.Kind == ScreenNodeKind.Panel)
                    DrawMesh(pixel, visual, local, Mix(new Color(20, 22, 26), tint));
                if (node.ImageId is { } imageId && assetTextures.TryGetValue(imageId, out var sprite))
                {
                    DrawMesh(sprite, visual, local, tint);
                }
                if (!rendered.TryGetValue((instance.Id, node.Id), out var item)) continue;
                var (region, texture) = item;
                var focused = screens.Stack.Focus is { } focus && focus.NodeId == node.Id && focus.InstanceId == instance.Id;
                var disabled = node.Kind == ScreenNodeKind.Button && !instance.Definition.Buttons.Any(n => n.Id == node.Id);
                var stateColor = disabled ? new Color(110, 115, 120) : focused ? new Color(160, 215, 255) : Color.White;
                DrawMesh(texture.Texture, visual, new(region.Raster.OffsetX, region.Raster.OffsetY, region.Raster.Width, region.Raster.Height), Mix(stateColor, tint));
            }
        }
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
    }
}
