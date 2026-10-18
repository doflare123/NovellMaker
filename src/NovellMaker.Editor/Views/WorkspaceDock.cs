using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace NovellMaker.Editor.Views;

/// <summary>Resizable split tree. Content controls survive hiding and reparenting.</summary>
internal sealed class WorkspaceDock
{
    private abstract class Node;
    private sealed class Pane(string id, string title, Control content) : Node
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public Control Content { get; } = content;
        public Border? Frame { get; set; }
    }
    private sealed class Split(Node first, Node second, bool horizontal, double ratio) : Node
    {
        public Node First { get; set; } = first;
        public Node Second { get; set; } = second;
        public bool Horizontal { get; } = horizontal;
        public double Ratio { get; set; } = ratio;
        public Grid? View { get; set; }
    }

    private readonly Grid host;
    private readonly MenuItem menu;
    private readonly Dictionary<string, Pane> panes = [];
    private Node? root;
    private Pane? dragging;
    private IPointer? capturedPointer;
    private Point dragStart;
    private bool dragActive;
    private readonly Border indicator = new()
    {
        Background = new SolidColorBrush(Color.Parse("#554F83AA")),
        BorderBrush = new SolidColorBrush(Color.Parse("#8DB7D5")),
        BorderThickness = new Thickness(2), IsHitTestVisible = false, IsVisible = false
    };

    public WorkspaceDock(Grid host, MenuItem menu)
    {
        this.host = host;
        this.menu = menu;
        host.Focusable = true;
        host.PointerMoved += PointerMoved;
        host.PointerReleased += PointerReleased;
        host.PointerCaptureLost += (_, _) => CancelDrag();
        host.KeyDown += (_, e) => { if (e.Key == Key.Escape && dragging is not null) { CancelDrag(); e.Handled = true; } };
    }

    public void Register(string id, string title, Control content)
    {
        // Keep inherited bindings stable even while controls are detached during docking.
        content.DataContext = host.DataContext;
        panes.Add(id, new Pane(id, title, content));
    }
    public bool IsVisible(string id) => Contains(root, panes[id]);

    public void Reset()
    {
        CancelDrag();
        var right = new Split(panes["scene"], panes["information"], false, .58);
        var editors = new Split(panes["documents"], right, true, .67);
        root = new Split(new Split(panes["explorer"], editors, true, .18), panes["messages"], false, .82);
        Render();
    }

    public void Hide(string id)
    {
        RememberSizes(root);
        root = Remove(root, panes[id]);
        Render();
    }

    public void Show(string id)
    {
        if (IsVisible(id)) return;
        RememberSizes(root);
        root = root is null ? panes[id] : new Split(root, panes[id], id != "messages", .75);
        Render();
    }

    // Direction is relative to the target panel; usable from both mouse and keyboard menus.
    public void Move(string id, string targetId, Dock side)
    {
        if (id == targetId || !IsVisible(targetId)) return;
        RememberSizes(root);
        var pane = panes[id];
        var target = panes[targetId];
        root = Remove(root, pane);
        var before = side is Dock.Left or Dock.Top;
        var split = new Split(before ? pane : target, before ? target : pane, side is Dock.Left or Dock.Right, .5);
        root = Replace(root!, target, split);
        Render();
    }

    private static bool Contains(Node? node, Pane pane) => ReferenceEquals(node, pane) ||
        node is Split s && (Contains(s.First, pane) || Contains(s.Second, pane));

    private static Node? Remove(Node? node, Pane pane)
    {
        if (ReferenceEquals(node, pane)) return null;
        if (node is not Split s) return node;
        var first = Remove(s.First, pane);
        var second = Remove(s.Second, pane);
        if (first is null) return second;
        if (second is null) return first;
        s.First = first; s.Second = second;
        return s;
    }

    private static Node Replace(Node node, Pane target, Node replacement)
    {
        if (ReferenceEquals(node, target)) return replacement;
        if (node is Split s) { s.First = Replace(s.First, target, replacement); s.Second = Replace(s.Second, target, replacement); }
        return node;
    }

    private static void RememberSizes(Node? node)
    {
        if (node is not Split s) return;
        if (s.View is { } grid)
        {
            var a = s.Horizontal ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
            var b = s.Horizontal ? grid.ColumnDefinitions[2].ActualWidth : grid.RowDefinitions[2].ActualHeight;
            if (a + b > 0) s.Ratio = Math.Clamp(a / (a + b), .05, .95);
        }
        RememberSizes(s.First); RememberSizes(s.Second);
    }

    private void Render()
    {
        // Reparent frames as a whole so their content and local control state can survive.
        foreach (var pane in panes.Values)
        {
            if (pane.Frame?.Parent is Panel old) old.Children.Remove(pane.Frame);
        }
        host.Children.Clear();
        if (root is not null)
        {
            var view = Build(root);
            Grid.SetColumn(view, 0); Grid.SetRow(view, 0);
            host.Children.Add(view);
        }
        else host.Children.Add(new TextBlock
        {
            Text = "Все панели скрыты. Откройте их через меню «Вид → Панели».",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        host.Children.Add(indicator);
        RebuildMenu();
    }

    private Control Build(Node node)
    {
        if (node is Pane pane)
        {
            if (pane.Frame is not null) return pane.Frame;
            var title = new TextBlock { Text = pane.Title, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var grip = new Border { Background = Brush("#2A2D32"), Child = title, Cursor = new Cursor(StandardCursorType.SizeAll) };
            ToolTip.SetTip(grip, "Перетащите к краю другой панели. Escape — отмена. Меню справа — закрепление.");
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
                dragging = pane; dragStart = e.GetPosition(host); dragActive = false;
                host.Focus(); capturedPointer = e.Pointer; e.Pointer.Capture(host); e.Handled = true;
            };
            var close = new Button { Content = "×", Padding = new Thickness(8, 2), Background = Brushes.Transparent };
            Avalonia.Automation.AutomationProperties.SetName(close, "Скрыть: " + pane.Title);
            ToolTip.SetTip(close, "Скрыть панель; вернуть через «Вид → Панели»");
            close.Click += (_, _) => Hide(pane.Id);
            var actions = new Button { Content = "…", Padding = new Thickness(8, 2), Background = Brushes.Transparent };
            Avalonia.Automation.AutomationProperties.SetName(actions, "Закрепить: " + pane.Title);
            actions.Click += (_, _) =>
            {
                var context = new ContextMenu();
                foreach (var target in panes.Values.Where(p => p != pane && IsVisible(p.Id)))
                {
                    var item = new MenuItem { Header = "Относительно: " + target.Title };
                    foreach (var (label, side) in Sides())
                    {
                        var choice = new MenuItem { Header = label };
                        choice.Click += (_, _) => Move(pane.Id, target.Id, side);
                        item.Items.Add(choice);
                    }
                    context.Items.Add(item);
                }
                actions.ContextMenu = context;
                context.Open(actions);
            };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 32 };
            Grid.SetColumn(actions, 1); Grid.SetColumn(close, 2);
            header.Children.Add(grip); header.Children.Add(actions); header.Children.Add(close);
            DockPanel.SetDock(header, Dock.Top);
            var body = new DockPanel(); body.Children.Add(header); body.Children.Add(pane.Content);
            pane.Frame = new Border { Child = body, BorderThickness = new Thickness(1), BorderBrush = Brush("#383C43"), ClipToBounds = true };
            return pane.Frame;
        }
        var s = (Split)node;
        var grid = new Grid();
        var first = Build(s.First); var second = Build(s.Second);
        // Frames are reused: attached Grid coordinates belong to the previous parent.
        Grid.SetColumn(first, 0); Grid.SetRow(first, 0);
        Grid.SetColumn(second, 0); Grid.SetRow(second, 0);
        var splitter = new GridSplitter { Background = Brush("#363A40"),
            ResizeDirection = s.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        if (s.Horizontal)
        {
            grid.ColumnDefinitions = new ColumnDefinitions
            {
                new(new GridLength(s.Ratio, GridUnitType.Star)) { MinWidth = 80 }, new(new GridLength(5)),
                new(new GridLength(1 - s.Ratio, GridUnitType.Star)) { MinWidth = 80 }
            };
            Grid.SetColumn(splitter, 1); Grid.SetColumn(second, 2);
        }
        else
        {
            grid.RowDefinitions = new RowDefinitions
            {
                new(new GridLength(s.Ratio, GridUnitType.Star)) { MinHeight = 45 }, new(new GridLength(5)),
                new(new GridLength(1 - s.Ratio, GridUnitType.Star)) { MinHeight = 45 }
            };
            Grid.SetRow(splitter, 1); Grid.SetRow(second, 2);
        }
        grid.Children.Add(first); grid.Children.Add(splitter); grid.Children.Add(second);
        s.View = grid;
        return grid;
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private static (string, Dock)[] Sides() => [("Слева", Dock.Left), ("Справа", Dock.Right), ("Сверху", Dock.Top), ("Снизу", Dock.Bottom)];

    private void RebuildMenu()
    {
        menu.Items.Clear();
        foreach (var pane in panes.Values)
        {
            var item = new MenuItem { Header = pane.Title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = IsVisible(pane.Id) };
            item.Click += (_, _) => { if (IsVisible(pane.Id)) Hide(pane.Id); else Show(pane.Id); };
            menu.Items.Add(item);
        }
    }

    private (Pane Pane, Dock Side, Rect Area)? Target(Point point)
    {
        foreach (var pane in panes.Values.Where(p => p != dragging && IsVisible(p.Id)))
        {
            if (pane.Frame?.TranslatePoint(default, host) is not { } origin) continue;
            var bounds = new Rect(origin, pane.Frame.Bounds.Size);
            if (!bounds.Contains(point)) continue;
            var x = (point.X - bounds.X) / bounds.Width;
            var y = (point.Y - bounds.Y) / bounds.Height;
            var nearest = new[] { (x, Dock.Left), (1-x, Dock.Right), (y, Dock.Top), (1-y, Dock.Bottom) }.MinBy(p => p.Item1).Item2;
            var area = nearest switch
            {
                Dock.Left => new Rect(bounds.X, bounds.Y, bounds.Width / 2, bounds.Height),
                Dock.Right => new Rect(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
                Dock.Top => new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2),
                _ => new Rect(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2)
            };
            return (pane, nearest, area);
        }
        return null;
    }

    private void PointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragging is null) return;
        var point = e.GetPosition(host);
        if (Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) > 5) dragActive = true;
        var target = dragActive ? Target(point) : null;
        indicator.IsVisible = target is not null;
        if (target is { } t)
        {
            indicator.HorizontalAlignment = HorizontalAlignment.Left; indicator.VerticalAlignment = VerticalAlignment.Top;
            indicator.Margin = new Thickness(t.Area.X, t.Area.Y, 0, 0);
            indicator.Width = t.Area.Width; indicator.Height = t.Area.Height;
        }
    }

    private void PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var source = dragging;
        var target = dragActive ? Target(e.GetPosition(host)) : null;
        CancelDrag(); e.Pointer.Capture(null);
        if (source is not null && target is { } t) Move(source.Id, t.Pane.Id, t.Side);
    }

    private void CancelDrag()
    {
        dragging = null; dragActive = false; indicator.IsVisible = false;
        var pointer = capturedPointer; capturedPointer = null; pointer?.Capture(null);
    }

    private sealed record LayoutNode(string? Pane, LayoutNode? First, LayoutNode? Second, bool Horizontal, double Ratio);
    private static LayoutNode? Snapshot(Node? node) => node switch
    {
        Pane p => new(p.Id, null, null, false, .5),
        Split s => new(null, Snapshot(s.First), Snapshot(s.Second), s.Horizontal, s.Ratio),
        _ => null
    };

    internal string ExportLayout()
    {
        RememberSizes(root);
        return JsonSerializer.Serialize(Snapshot(root));
    }

    internal void ImportLayout(string json)
    {
        var data = JsonSerializer.Deserialize<LayoutNode>(json, new JsonSerializerOptions { MaxDepth = 16 });
        var used = new HashSet<string>();
        Node? Read(LayoutNode? node)
        {
            if (node is null) return null;
            if (node.Pane is { } id)
            {
                if (!panes.TryGetValue(id, out var pane) || !used.Add(id) || node.First is not null || node.Second is not null)
                    throw new InvalidDataException("Некорректная панель в сохранённом расположении.");
                return pane;
            }
            if (!double.IsFinite(node.Ratio) || node.Ratio is < .05 or > .95 ||
                Read(node.First) is not { } first || Read(node.Second) is not { } second)
                throw new InvalidDataException("Некорректный разделитель в сохранённом расположении.");
            return new Split(first, second, node.Horizontal, node.Ratio);
        }
        var restored = Read(data);
        CancelDrag(); root = restored; Render();
    }
}
