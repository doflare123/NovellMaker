using System.Collections.Immutable;
using System.Globalization;

namespace NovellMaker.Core.Screens;

public readonly record struct ScreenPoint(double X, double Y);

/// <summary>Double precision affine matrix: x'=A*x+C*y+X, y'=B*x+D*y+Y.</summary>
public readonly record struct ScreenMatrix(double A, double B, double C, double D, double X, double Y)
{
    public static ScreenMatrix Identity => new(1, 0, 0, 1, 0, 0);
    public ScreenPoint Apply(double x, double y) => new(A * x + C * y + X, B * x + D * y + Y);
    public ScreenMatrix Then(ScreenMatrix parent) => new(
        parent.A * A + parent.C * B, parent.B * A + parent.D * B,
        parent.A * C + parent.C * D, parent.B * C + parent.D * D,
        parent.A * X + parent.C * Y + parent.X, parent.B * X + parent.D * Y + parent.Y);
    public ScreenMatrix Inverse()
    {
        var determinant = A * D - B * C;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            throw new ArgumentException("Вырожденное преобразование узла.");
        return new(D / determinant, -B / determinant, -C / determinant, A / determinant,
            (C * Y - D * X) / determinant, (B * X - A * Y) / determinant);
    }
}

public sealed record ScreenGeometry
{
    [System.Text.Json.Serialization.JsonRequired] public double AnchorX { get; init; }
    [System.Text.Json.Serialization.JsonRequired] public double AnchorY { get; init; }
    [System.Text.Json.Serialization.JsonRequired] public double PivotX { get; init; }
    [System.Text.Json.Serialization.JsonRequired] public double PivotY { get; init; }
    [System.Text.Json.Serialization.JsonRequired] public double ScaleX { get; init; } = 1;
    [System.Text.Json.Serialization.JsonRequired] public double ScaleY { get; init; } = 1;
    [System.Text.Json.Serialization.JsonRequired] public double Rotation { get; init; }
    [System.Text.Json.Serialization.JsonRequired] public double Opacity { get; init; } = 1;

    internal bool IsValid => Unit(AnchorX) && Unit(AnchorY) && Unit(PivotX) && Unit(PivotY) && Unit(Opacity) &&
        Scale(ScaleX) && Scale(ScaleY) && double.IsFinite(Rotation) && Math.Abs(Rotation) <= 360000;
    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool Scale(double value) => double.IsFinite(value) && Math.Abs(value) is >= .001 and <= 1000;
    internal ScreenMatrix Matrix(ScreenRect bounds, double parentWidth, double parentHeight)
    {
        var angle = Rotation % 360 * Math.PI / 180;
        var a = Math.Cos(angle) * ScaleX; var b = Math.Sin(angle) * ScaleX;
        var c = -Math.Sin(angle) * ScaleY; var d = Math.Cos(angle) * ScaleY;
        var px = bounds.Width * PivotX; var py = bounds.Height * PivotY;
        return new(a, b, c, d, bounds.X + AnchorX * parentWidth + px - a * px - c * py,
            bounds.Y + AnchorY * parentHeight + py - b * px - d * py);
    }
}

public readonly record struct ScreenTint(double R, double G, double B)
{
    public static ScreenTint White => new(1, 1, 1);
    public static bool TryParse(string? color, out ScreenTint tint)
    {
        tint = White;
        if (color is not { Length: 7 } || color[0] != '#' ||
            !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        tint = new((rgb >> 16) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d); return true;
    }
    internal ScreenTint Multiply(ScreenTint other) => new(R * other.R, G * other.G, B * other.B);
}

/// <summary>Convex geometry shared by rendering and input; mirrored rectangles retain winding.</summary>
public static class ScreenPolygon
{
    public static ImmutableArray<ScreenPoint> Rectangle(ScreenRect rect) =>
        [new(rect.X, rect.Y), new(rect.X + rect.Width, rect.Y), new(rect.X + rect.Width, rect.Y + rect.Height), new(rect.X, rect.Y + rect.Height)];
    public static ImmutableArray<ScreenPoint> Transform(ImmutableArray<ScreenPoint> points, ScreenMatrix matrix) =>
        [.. points.Select(p => matrix.Apply(p.X, p.Y))];
    public static ScreenRect Bounds(ImmutableArray<ScreenPoint> points)
    {
        if (points.IsEmpty) return new(0, 0, 0, 0);
        var x = points.Min(p => p.X); var y = points.Min(p => p.Y);
        return new(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }
    public static double Area(ImmutableArray<ScreenPoint> points)
    {
        var sum = 0d;
        if (points.IsEmpty) return sum;
        var origin = points[0];
        for (var i = 1; i < points.Length - 1; i++)
        { var a = points[i]; var b = points[i + 1]; sum += (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X); }
        return sum / 2;
    }
    public static ImmutableArray<ScreenPoint> Clip(ImmutableArray<ScreenPoint> subject, ImmutableArray<ScreenPoint> clip)
    {
        var winding = Math.Sign(Area(clip));
        if (winding == 0) return [];
        for (var edge = 0; edge < clip.Length && !subject.IsEmpty; edge++)
        {
            var a = clip[edge]; var b = clip[(edge + 1) % clip.Length];
            var length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            if (length < 1e-10) continue;
            double Side(ScreenPoint p)
            {
                var side = winding * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / length;
                return Math.Abs(side) < 1e-8 ? 0 : side;
            }
            var output = ImmutableArray.CreateBuilder<ScreenPoint>();
            var previous = subject[^1]; var before = Side(previous);
            foreach (var point in subject)
            {
                var after = Side(point);
                if ((before >= 0) != (after >= 0))
                {
                    var t = before / (before - after);
                    Add(new(previous.X + (point.X - previous.X) * t, previous.Y + (point.Y - previous.Y) * t));
                }
                if (after >= 0) Add(point);
                previous = point; before = after;
            }
            if (output.Count > 1 && Near(output[0], output[^1])) output.RemoveAt(output.Count - 1);
            subject = output.ToImmutable();
            void Add(ScreenPoint point) { if (output.Count == 0 || !Near(output[^1], point)) output.Add(point); }
        }
        return Math.Abs(Area(subject)) > 1e-8 ? subject : [];
    }
    private static bool Near(ScreenPoint a, ScreenPoint b) => Math.Abs(a.X - b.X) < 1e-8 && Math.Abs(a.Y - b.Y) < 1e-8;
}

public sealed record ScreenVisual(ScreenNode Node, ScreenMatrix World, ScreenMatrix Inverse,
    ImmutableArray<ScreenPoint> Polygon, double Opacity, ScreenTint Tint, bool Enabled)
{
    public ScreenRect Bounds => ScreenPolygon.Bounds(Polygon);
    public ScreenPoint ToLocal(double x, double y) => Inverse.Apply(x, y);
}
