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

internal sealed record ComponentDrop(ScreenNodeKind Kind, Guid? ImageId = null)
{
    internal string Encode(Guid project) => $"novellmaker-component:{project:D}:{(int)Kind}:{ImageId?.ToString("D") ?? ""}";
    internal static ComponentDrop? Decode(string? payload, EditorSession session)
    {
        var parts = payload?.Split(':');
        return parts is { Length: 4 } && parts[0] == "novellmaker-component" && Guid.TryParse(parts[1], out var project) && project == session.Project?.Id &&
            int.TryParse(parts[2], out var kind) && Enum.IsDefined((ScreenNodeKind)kind) &&
            (parts[3] == "" || Guid.TryParse(parts[3], out var id) && session.Project.Images.Any(i => i.Id == id))
            ? new((ScreenNodeKind)kind, parts[3] == "" ? null : Guid.Parse(parts[3])) : null;
    }
}

public sealed class ComponentToolbox : UserControl
{
    private EditorSession? session;
    private bool refreshing;
    private readonly TreeView layers = new() { Name = "ScreenNodes", MinHeight = 120 };
    private readonly StackPanel images = new() { Spacing = 4 };
    private readonly StackPanel body = new() { Spacing = 5, Margin = new Thickness(10) };
    public ComponentToolbox()
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = "Компоненты", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new HelpTip { Explanation = "Перетащите компонент на холст. Двойной щелчок добавляет его в центр. Изображения ниже — файлы этого проекта." });
        body.Children.Add(title);
        layers.SelectionChanged += (_, _) => { if (!refreshing) session?.SelectScreenNode(((layers.SelectedItem as TreeViewItem)?.DataContext as ScreenNode)?.Id); };
        foreach (var (kind, label, glyph) in new[] {
            (ScreenNodeKind.Text, "Текст", "T"), (ScreenNodeKind.Button, "Кнопка", "▭"), (ScreenNodeKind.Image, "Изображение", "▧"),
            (ScreenNodeKind.Panel, "Панель", "□"), (ScreenNodeKind.Container, "Контейнер", "⊞") })
            body.Children.Add(Item(label, glyph, new(kind)));
        body.Children.Add(new Border { Height = 1, Background = Brush.Parse("#3A3E45"), Margin = new Thickness(0, 8) });
        body.Children.Add(new TextBlock { Text = "Изображения проекта", FontWeight = FontWeight.SemiBold, FontSize = 12 }); body.Children.Add(images);
        var import = new Button { Name = "ToolboxImportImage", Content = "Импортировать…", HorizontalAlignment = HorizontalAlignment.Stretch };
        import.Click += async (_, _) => { if (session is not null) await session.ImportDesignerImageAsync(); }; body.Children.Add(import);
        body.Children.Add(new TextBlock { Text = "Структура сцены", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) }); body.Children.Add(layers);
        Content = new ScrollViewer { Content = body };
        DataContextChanged += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); };
        AttachedToVisualTree += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); };
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private Control Item(string label, string glyph, ComponentDrop data)
    {
        var row = new Grid { ColumnDefinitions = new("26,*"), Margin = new Thickness(8, 7), IsHitTestVisible = false };
        row.Children.Add(new TextBlock { Text = glyph, Foreground = Brush.Parse("#AFC5DF"), FontSize = 17 });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(text, 1); row.Children.Add(text);
        var item = new Border { Name = "Toolbox" + data.Kind + (data.ImageId is null ? "" : data.ImageId.Value.ToString("N")), Child = row,
            Background = Brush.Parse("#2C3036"), CornerRadius = new CornerRadius(3), Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
        ToolTip.SetTip(item, label);
        PointerPressedEventArgs? press = null; Point start = default;
        item.PointerPressed += (_, e) => { if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed && session?.IsScreenDesigner == true) { press = e; start = e.GetPosition(item); } };
        item.PointerMoved += async (_, e) =>
        {
            if (press is null || !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed || session?.CanEdit != true ||
                Math.Abs(e.GetPosition(item).X - start.X) + Math.Abs(e.GetPosition(item).Y - start.Y) < 5) return;
            var gesture = press; press = null;
            using var transfer = new DataTransfer(); transfer.Add(DataTransferItem.CreateText(data.Encode(session.Project!.Id)));
            await DragDrop.DoDragDropAsync(gesture, transfer, DragDropEffects.Copy);
        };
        item.PointerReleased += (_, _) => press = null;
        item.DoubleTapped += (_, _) => { if (session?.IsScreenDesigner == true) session.AddScreenNodeAt(data.Kind, 480, 300, data.ImageId); };
        item.KeyDown += (_, e) => { if (e.Key == Key.Enter && session?.IsScreenDesigner == true) { session.AddScreenNodeAt(data.Kind, 480, 300, data.ImageId); e.Handled = true; } };
        return item;
    }
    private void Subscribe() { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } }
    private void Unsubscribe() { if (session is not null) session.PropertyChanged -= Changed; }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(EditorSession.Project) or nameof(EditorSession.IsScreenDesigner) or nameof(EditorSession.CanEdit) or nameof(EditorSession.SelectedScreen) or nameof(EditorSession.SelectedScreenNode)) Refresh(); }
    private void Refresh()
    {
        refreshing = true;
        try
        {
            var nodes = session?.SelectedScreen?.Nodes ?? [];
            TreeViewItem? selected = null;
            TreeViewItem Branch(ScreenNode node)
            {
                var item = new TreeViewItem { DataContext = node, Header = node.Name.Length > 0 ? node.Name : ComponentName(node), IsExpanded = true,
                    ItemsSource = nodes.Where(n => n.ParentId == node.Id).Select(Branch).ToArray() };
                if (node.Id == session?.SelectedScreenNodeId) selected = item;
                return item;
            }
            layers.ItemsSource = nodes.Where(n => n.ParentId is null).Select(Branch).ToArray();
            layers.SelectedItem = selected;
            body.IsEnabled = session?.CanEdit == true && session.IsScreenDesigner;
            images.Children.Clear();
            foreach (var image in session?.Project?.Images ?? []) images.Children.Add(Item(image.Name, "▧", new(ScreenNodeKind.Image, image.Id)));
        }
        finally { refreshing = false; }
    }
    private static string ComponentName(ScreenNode node) => ScreenPropertiesView.ComponentName(node.Kind) + (node.Text.Length > 0 ? " · " + node.Text : "");
}
