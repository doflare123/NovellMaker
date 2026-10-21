using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed record ScreenRaster(string Text, int Width, int Height, int OffsetX, int OffsetY, double LogicalHeight, bool Transparent = false);
internal sealed record ScreenRasterNode(ScreenNode Node, ScreenRaster Raster, int X, int Y)
{
    public Guid InstanceId { get; init; }
}

/// <summary>Device-independent allocation policy. Coordinates are clipped before any bitmap allocation.</summary>
internal sealed class ScreenTextureCache<T>(Func<ScreenRaster, T> render) : IDisposable where T : IDisposable
{
    internal const int MaximumTextureSide = 2048;
    internal const int MaximumTexturePixels = 256 * 1024;
    internal const int MaximumCachedPixels = 4 * 1024 * 1024;
    internal const int MaximumFramePixels = 256 * 1024;
    internal const int MaximumFrameTextures = 4;
    private readonly Dictionary<(Guid Instance, Guid Node), (ScreenRaster Raster, T Texture)> textures = [];
    private (Guid Id, ScreenDefinition Definition)[]? layers;
    private ScreenDefinition? plannedDefinition;
    private ScreenRect plannedViewport;
    private List<ScreenRasterNode> planned = [];
    internal int RejectedNodes { get; private set; }
    internal int DeferredNodes { get; private set; }
    internal int CachedPixels => textures.Values.Sum(t => t.Raster.Width * t.Raster.Height);

    internal IReadOnlyList<(ScreenRasterNode Region, T Texture)> Update(ScreenDefinition? definition, ScreenRect viewport)
    {
        if (layers is not null || !ReferenceEquals(plannedDefinition, definition) || plannedViewport != viewport)
        {
            layers = null;
            plannedDefinition = definition; plannedViewport = viewport;
            RejectedNodes = 0;
            planned = definition is null ? [] : Plan(definition, viewport, () => RejectedNodes++);
        }
        return Upload();
    }

    internal IReadOnlyList<(ScreenRasterNode Region, T Texture)> UpdateLayers(IReadOnlyList<ScreenInstance> instances, ScreenRect viewport)
    {
        if (layers is null || plannedViewport != viewport || layers.Length != instances.Count ||
            layers.Where((layer, index) => layer.Id != instances[index].Id || !ReferenceEquals(layer.Definition, instances[index].Definition)).Any())
        {
            layers = instances.Select(i => (i.Id, i.Definition)).ToArray(); plannedViewport = viewport;
            RejectedNodes = 0;
            planned = layers.SelectMany(layer => Plan(layer.Definition, viewport, () => RejectedNodes++)
                .Select(node => node with { InstanceId = layer.Id })).ToList();
        }
        return Upload();
    }

    private IReadOnlyList<(ScreenRasterNode Region, T Texture)> Upload()
    {
        var desired = planned;
        DeferredNodes = 0;
        var keys = desired.ToDictionary(n => (n.InstanceId, n.Node.Id));
        foreach (var id in textures.Keys.ToArray())
            if (!keys.TryGetValue(id, out var node) || textures[id].Raster != node.Raster)
            { textures[id].Texture.Dispose(); textures.Remove(id); }
        var pixels = 0;
        var count = 0;
        var cached = CachedPixels;
        var result = new List<(ScreenRasterNode, T)>();
        foreach (var node in desired)
        {
            var key = (node.InstanceId, node.Node.Id);
            if (!textures.TryGetValue(key, out var item))
            {
                var area = node.Raster.Width * node.Raster.Height;
                if (count >= MaximumFrameTextures || pixels + area > MaximumFramePixels || cached + area > MaximumCachedPixels)
                { DeferredNodes++; continue; }
                item = (node.Raster, render(node.Raster));
                textures.Add(key, item);
                count++; pixels += area; cached += area;
            }
            result.Add((node, item.Texture));
        }
        return result;
    }

    internal static List<ScreenRasterNode> Plan(ScreenDefinition definition, ScreenRect viewport, Action? reject = null)
    {
        var result = new List<ScreenRasterNode>();
        foreach (var visual in definition.Visuals(viewport).Where(v => v.Node.Kind is ScreenNodeKind.Text or ScreenNodeKind.Button))
        {
            var node = visual.Node;
            var local = ScreenPolygon.Bounds(ScreenPolygon.Transform(visual.Polygon, visual.Inverse));
            var left = Math.Max(0, (int)Math.Floor(local.X + 1e-8)); var top = Math.Max(0, (int)Math.Floor(local.Y + 1e-8));
            var width = (int)Math.Ceiling(Math.Min(node.Bounds.Width, local.X + local.Width) - 1e-8) - left;
            var height = (int)Math.Ceiling(Math.Min(node.Bounds.Height, local.Y + local.Height) - 1e-8) - top;
            if (width <= 0 || height <= 0) continue;
            if (width > MaximumTextureSide || height > MaximumTextureSide || (long)width * height > MaximumTexturePixels)
            { reject?.Invoke(); continue; }
            var origin = visual.World.Apply(left, top);
            result.Add(new(node, new(node.Text, width, height, left, top, node.Bounds.Height, node.ImageId is not null || node.Kind == ScreenNodeKind.Text), (int)origin.X, (int)origin.Y));
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var item in textures.Values) item.Texture.Dispose();
        textures.Clear();
        plannedDefinition = null; planned.Clear();
        layers = null;
    }
}
