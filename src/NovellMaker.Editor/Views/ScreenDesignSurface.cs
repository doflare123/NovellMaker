using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

public sealed class ScreenDesignSurface : UserControl
{
    private EditorSession? session;
    private readonly DialogueStyleSample scenePreview = new() { Width = 1280, Height = 720, CanvasOnly = true, IsHitTestVisible = false, ZIndex = -1 };
    private readonly Canvas artboard = new() { Name = "ScreenArtboard", Width = 1280, Height = 720, Background = Brush.Parse("#15171B"), ClipToBounds = true, Focusable = true };
    private readonly Viewbox viewport = new() { Name = "ScreenViewport", Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private double? zoom;
    private readonly SelectionOverlay selection = new() { Width = 1280, Height = 720, IsHitTestVisible = false };
    private readonly Dictionary<Guid, (Border Clip, Control Visual, ScreenNode Node)> visuals = [];
    private (Guid Project, Guid Screen, ScreenNode Before, Point Start, bool Resize)? gesture;
    private ScreenRect? temporary;
    private IPointer? captured;
    internal Canvas Artboard => artboard;

    public ScreenDesignSurface()
    {
        artboard.Children.Add(scenePreview);
        viewport.Child = artboard;
        Content = new Border { Background = Brush.Parse("#1D2025"), Padding = new Thickness(20), Child = new ScrollViewer
        { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = viewport } };
        SizeChanged += (_, _) => ResizeViewport();
        artboard.AddHandler(PointerPressedEvent, Press, RoutingStrategies.Tunnel);
        artboard.AddHandler(PointerMovedEvent, Move, RoutingStrategies.Tunnel);
        artboard.AddHandler(PointerReleasedEvent, Release, RoutingStrategies.Tunnel);
        artboard.PointerCaptureLost += (_, _) => Cancel();
        artboard.KeyDown += HandleKey;
        artboard.KeyUp += (_, _) => session?.EndEditGroup();
        DragDrop.SetAllowDrop(artboard, true);
        artboard.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DropData(e) is null ? DragDropEffects.None : DragDropEffects.Copy; e.Handled = true; });
        artboard.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DropData(e) is not { } data || session is null) return;
            var point = e.GetPosition(artboard);
            session.AddScreenNodeAt(data.Kind, point.X, point.Y, data.ImageId); artboard.Focus(); e.Handled = true;
        });
        DataContextChanged += (_, _) => { Unsubscribe(); Cancel(); session = DataContext as EditorSession; Subscribe(); Refresh(); };
        AttachedToVisualTree += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); };
        DetachedFromVisualTree += (_, _) => { Cancel(); Unsubscribe(); };
    }
    public void SetZoom(double? value) { zoom = value; ResizeViewport(); }
    private void ResizeViewport()
    {
        viewport.Width = zoom is { } scale ? 1280 * scale : Math.Max(1, Bounds.Width - 40);
        viewport.Height = zoom is { } factor ? 720 * factor : Math.Max(1, Bounds.Height - 40);
    }
    private ComponentDrop? DropData(DragEventArgs e) => session?.CanEdit == true && session.SelectedScreen is not null
        ? ComponentDrop.Decode(e.DataTransfer.TryGetText(), session) : null;
    private void Subscribe() { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } }
    private void Unsubscribe() { if (session is not null) session.PropertyChanged -= Changed; }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSession.ActiveScene) or nameof(EditorSession.SelectedScreen) or nameof(EditorSession.SelectedScreenNode) or nameof(EditorSession.AssetRevision) or nameof(EditorSession.ProjectDirectory) or nameof(EditorSession.CanEdit))
        {
            if (e.PropertyName is nameof(EditorSession.AssetRevision) or nameof(EditorSession.ProjectDirectory))
            { foreach (var item in visuals.Values) artboard.Children.Remove(item.Clip); visuals.Clear(); }
            if (gesture is { } g && (session?.Project?.Id != g.Project || session.SelectedScreen?.Id != g.Screen || session.SelectedScreenNode?.Id != g.Before.Id || session.SelectedScreenNode != g.Before || !session.CanEdit)) Cancel();
            Refresh();
        }
    }
    private ScreenDefinition? Definition()
    {
        if (session?.SelectedScreen is not { } screen) return null;
        return temporary is { } bounds && gesture is { } g ? new(screen.Id, screen.Nodes.Replace(g.Before, g.Before with { Bounds = bounds })) : screen.Definition();
    }
    private void Refresh()
    {
        scenePreview.DataContext = session;
        scenePreview.StyleSheet = session?.ActiveScene?.Style;
        scenePreview.SampleFrame = session?.ActiveScene?.SampleFrame;
        scenePreview.SampleText = session?.ActiveScene?.SampleText ?? "";
        scenePreview.BackgroundImage = session?.ActiveScene?.BackgroundImage;
        scenePreview.ProjectDirectory = session?.ProjectDirectory;
        scenePreview.AssetRevision = session?.AssetRevision ?? 0;
        scenePreview.ShowVoiceProgress = session?.ShowVoiceProgress == true;
        scenePreview.ShowVoiceControls = session?.ShowVoiceControls == true;
        scenePreview.ShowNavigationControls = session?.ShowNavigationControls == true;
        var definition = Definition();
        var wanted = definition?.Visuals(new(0, 0, 1280, 720)).ToArray() ?? [];
        foreach (var stale in visuals.Keys.Where(id => !wanted.Any(r => r.Node.Id == id)).ToArray()) { artboard.Children.Remove(visuals[stale].Clip); visuals.Remove(stale); }
        artboard.Children.Remove(selection);
        var z = 0;
        foreach (var visual in wanted)
        {
            var node = visual.Node;
            var tinted = node with { Color = $"#{(byte)(visual.Tint.R * 255):X2}{(byte)(visual.Tint.G * 255):X2}{(byte)(visual.Tint.B * 255):X2}" };
            if (!visuals.TryGetValue(node.Id, out var item) || item.Node.Text != node.Text || item.Node.Kind != node.Kind || item.Node.ImageId != node.ImageId || item.Node.Color != tinted.Color)
            {
                if (item.Clip is not null) artboard.Children.Remove(item.Clip);
                var inner = CreateNode(tinted);
                var clipping = new Border { IsHitTestVisible = false, Child = new Canvas { Children = { inner } } };
                item = (clipping, inner, tinted); visuals[node.Id] = item; artboard.Children.Add(clipping);
            }
            item.Clip.Width = 1280; item.Clip.Height = 720; item.Clip.ZIndex = z++;
            item.Clip.Clip = Polygon(visual.Polygon); item.Clip.Opacity = visual.Opacity;
            item.Visual.Width = node.Bounds.Width; item.Visual.Height = node.Bounds.Height;
            var m = visual.World;
            item.Visual.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
            item.Visual.RenderTransform = new MatrixTransform(new Matrix(m.A, m.B, m.C, m.D, m.X, m.Y));
        }
        selection.Selected = session?.SelectedScreenNode is { } selected && definition?.Nodes.Any(n => n.Id == selected.Id) == true ? definition.Visual(selected.Id) : null;
        selection.Resizable = session?.SelectedScreenNode?.ParentId is not null; selection.ZIndex = z;
        artboard.Children.Add(selection); selection.InvalidateVisual();
    }
    private static StreamGeometry Polygon(System.Collections.Immutable.ImmutableArray<ScreenPoint> points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        if (!points.IsEmpty)
        { context.BeginFigure(new Point(points[0].X, points[0].Y), true); foreach (var p in points.Skip(1)) context.LineTo(new Point(p.X, p.Y)); context.EndFigure(true); }
        return geometry;
    }
    private Control CreateNode(ScreenNode node)
    {
        ScreenTint.TryParse(node.Color, out var tint);
        var baseColor = node.Kind == ScreenNodeKind.Button ? Color.Parse("#282D34") : Color.Parse("#14161A");
        var grid = new Grid { Background = node.Kind is ScreenNodeKind.Panel or ScreenNodeKind.Button ? new SolidColorBrush(Color.FromRgb((byte)(baseColor.R * tint.R), (byte)(baseColor.G * tint.G), (byte)(baseColor.B * tint.B))) : Brushes.Transparent };
        if (node.ImageId is { } id && session?.Project?.Images.FirstOrDefault(i => i.Id == id) is { } image)
            grid.Children.Add(new ProjectImageView { Asset = image, ProjectDirectory = session.ProjectDirectory, Revision = session.AssetRevision,
                ImageStretch = Stretch.Fill, TransparentBackground = true, Tint = node.Color, IsHitTestVisible = false });
        else if (node.Kind == ScreenNodeKind.Image)
        {
            grid.Background = Brush.Parse("#293441");
            grid.Children.Add(new TextBlock { Text = "Изображение", Foreground = Brush.Parse("#91A4BB"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        if (node.Kind is ScreenNodeKind.Text or ScreenNodeKind.Button)
            grid.Children.Add(new TextBlock { Text = node.Text, Foreground = Brush.Parse(node.Color), FontSize = 18, TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }
    private void Press(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(artboard).Properties.IsLeftButtonPressed || session?.CanEdit != true || session.SelectedScreen is not { } screen) return;
        artboard.Focus(); var point = e.GetPosition(artboard); var definition = screen.Definition();
        var resize = session.SelectedScreenNode is { ParentId: not null } current && NearCorner(point, definition.Visual(current.Id));
        var id = resize ? session.SelectedScreenNodeId : definition.PickNode(point.X, point.Y);
        session.SelectScreenNode(id);
        if (session.SelectedScreenNode is { ParentId: not null } node)
        {
            gesture = (session.Project!.Id, screen.Id, node, point, resize); temporary = null; captured = e.Pointer; e.Pointer.Capture(artboard);
        }
        e.Handled = true;
    }
    private static bool NearCorner(Point point, ScreenVisual visual)
    { var corner = visual.World.Apply(visual.Node.Bounds.Width, visual.Node.Bounds.Height); return Math.Abs(point.X - corner.X) <= 14 && Math.Abs(point.Y - corner.Y) <= 14; }
    private void Move(object? sender, PointerEventArgs e)
    {
        if (gesture is not { } g) return;
        var point = e.GetPosition(artboard); var definition = session!.SelectedScreen!.Definition();
        var inverse = definition.Visual(g.Resize ? g.Before.Id : g.Before.ParentId!.Value).Inverse;
        var start = inverse.Apply(g.Start.X, g.Start.Y); var end = inverse.Apply(point.X, point.Y);
        var before = g.Before.Bounds;
        var candidate = g.Resize ? ScreenTree.Resize(g.Before, end.X - start.X, end.Y - start.Y)
            : before with { X = Math.Clamp(Math.Round(before.X + end.X - start.X), -100000, 100000), Y = Math.Clamp(Math.Round(before.Y + end.Y - start.Y), -100000, 100000) };
        try { _ = new ScreenDefinition(definition.Id, definition.Nodes.Replace(g.Before, g.Before with { Bounds = candidate })); temporary = candidate; }
        catch (ArgumentException) { } // Keep the last valid gesture preview; no partially applied edit.
        Refresh(); e.Handled = true;
    }
    private void Release(object? sender, PointerReleasedEventArgs e)
    {
        if (gesture is not { } g) return;
        var bounds = temporary; gesture = null; temporary = null; captured = null; e.Pointer.Capture(null);
        if (bounds is { } value && value != g.Before.Bounds && session?.Project?.Id == g.Project && session.SelectedScreen?.Id == g.Screen && session.SelectedScreenNode == g.Before)
            session.UpdateScreenNode(g.Before with { Bounds = value });
        Refresh(); e.Handled = true;
    }
    private void Cancel()
    {
        var pointer = captured; captured = null; gesture = null; temporary = null; pointer?.Capture(null);
        Refresh();
    }
    private async void HandleKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && gesture is not null) { Cancel(); e.Handled = true; return; }
        if (session?.CanEdit != true || session.SelectedScreenNode is not { ParentId: not null } node) return;
        if (e.Key == Key.Delete) { await session.DeleteScreenNodeAsync(node.Id); e.Handled = true; return; }
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var bounds = node.Bounds;
        var changed = e.Key switch { Key.Left => bounds with { X = bounds.X - step }, Key.Right => bounds with { X = bounds.X + step }, Key.Up => bounds with { Y = bounds.Y - step }, Key.Down => bounds with { Y = bounds.Y + step }, _ => bounds };
        if (changed != bounds) { session.UpdateScreenNode(node with { Bounds = changed }, "screen-nudge:" + node.Id); e.Handled = true; }
    }
    private sealed class SelectionOverlay : Control
    {
        public ScreenVisual? Selected { get; set; }
        public bool Resizable { get; set; }
        public override void Render(DrawingContext context)
        {
            if (Selected is not { } visual) return;
            var polygon = ScreenPolygon.Transform(ScreenPolygon.Rectangle(new(0, 0, visual.Node.Bounds.Width, visual.Node.Bounds.Height)), visual.World);
            context.DrawGeometry(null, new Pen(Brush.Parse("#79B5F3"), 2), Polygon(polygon));
            if (Resizable) context.DrawRectangle(Brushes.White, new Pen(Brush.Parse("#438EDC"), 2), new Rect(polygon[2].X - 7, polygon[2].Y - 7, 14, 14));
        }
    }
}
