using Microsoft.Xna.Framework;

namespace NovellMaker.Runtime;

internal enum RuntimeAction
{
    Next,
    Previous,
}

internal static class RuntimeInputMapper
{
    public const int LogicalWidth = 1280;
    public const int LogicalHeight = 720;

    public static Rectangle BackButtonBounds { get; } = new(976, 668, 140, 40);

    public static Rectangle NextButtonBounds { get; } = new(1124, 668, 140, 40);

    public static Rectangle NavigationBounds { get; } = new(
        BackButtonBounds.Left,
        BackButtonBounds.Top,
        NextButtonBounds.Right - BackButtonBounds.Left,
        BackButtonBounds.Height);

    public static Rectangle CalculateLetterbox(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return new Rectangle(0, 0, LogicalWidth, LogicalHeight);
        }

        var scale = Math.Min(width / (float)LogicalWidth, height / (float)LogicalHeight);
        var destinationWidth = Math.Max(1, (int)Math.Round(LogicalWidth * scale));
        var destinationHeight = Math.Max(1, (int)Math.Round(LogicalHeight * scale));
        return new Rectangle(
            (width - destinationWidth) / 2,
            (height - destinationHeight) / 2,
            destinationWidth,
            destinationHeight);
    }

    public static bool TryMapWindowToLogical(Point windowPoint, Rectangle letterbox, out Point logicalPoint)
    {
        if (letterbox.Width <= 0 || letterbox.Height <= 0 || !letterbox.Contains(windowPoint))
        {
            logicalPoint = default;
            return false;
        }

        logicalPoint = new Point(
            Math.Clamp(
                (int)((windowPoint.X - letterbox.X) * (long)LogicalWidth / letterbox.Width),
                0,
                LogicalWidth - 1),
            Math.Clamp(
                (int)((windowPoint.Y - letterbox.Y) * (long)LogicalHeight / letterbox.Height),
                0,
                LogicalHeight - 1));
        return true;
    }

    public static RuntimeAction? HitTestNavigation(
        Point logicalPoint,
        bool canPrevious,
        bool canNext)
    {
        if (canPrevious && BackButtonBounds.Contains(logicalPoint))
        {
            return RuntimeAction.Previous;
        }

        if (canNext && NextButtonBounds.Contains(logicalPoint))
        {
            return RuntimeAction.Next;
        }

        return null;
    }
}
