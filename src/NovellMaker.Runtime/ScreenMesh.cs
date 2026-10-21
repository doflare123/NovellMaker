using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

/// <summary>Triangulates the exact Core clipping polygon, with UVs from the same inverse as input.</summary>
internal static class ScreenMesh
{
    internal static VertexPositionColorTexture[] Build(ScreenVisual visual, ScreenRect textureRect, Color color)
    {
        var polygon = ScreenPolygon.Clip(visual.Polygon,
            ScreenPolygon.Transform(ScreenPolygon.Rectangle(textureRect), visual.World));
        var vertices = new VertexPositionColorTexture[Math.Max(0, polygon.Length - 2) * 3];
        VertexPositionColorTexture Vertex(ScreenPoint point)
        {
            var local = visual.ToLocal(point.X, point.Y);
            return new(new Vector3((float)point.X, (float)point.Y, 0), color,
                new Vector2((float)((local.X - textureRect.X) / textureRect.Width), (float)((local.Y - textureRect.Y) / textureRect.Height)));
        }
        for (var i = 1; i < polygon.Length - 1; i++)
        { var at = (i - 1) * 3; vertices[at] = Vertex(polygon[0]); vertices[at + 1] = Vertex(polygon[i]); vertices[at + 2] = Vertex(polygon[i + 1]); }
        return vertices;
    }
}
