using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

/// <summary>Resource identity, rather than a mutable name extension, determines the icon.</summary>
public sealed class ResourceIcon : UserControl
{
    public static readonly StyledProperty<ExplorerEntry?> EntryProperty = AvaloniaProperty.Register<ResourceIcon, ExplorerEntry?>(nameof(Entry));
    public ExplorerEntry? Entry { get => GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == EntryProperty) RenderIcon(); }
    private void RenderIcon()
    {
        var grid = new Grid { Width = 64, Height = 56 };
        if (Entry?.IconKind == "lua")
        {
            grid.Children.Add(new Ellipse { Width = 46, Height = 46, Fill = Brush.Parse("#263D7A"), Stroke = Brush.Parse("#A8BCE7"), StrokeThickness = 1.5 });
            grid.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Margin = new Thickness(24, -20, 0, 0) });
            grid.Children.Add(new TextBlock { Text = "Lua", FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 9, 0, 0) });
        }
        else
        {
            var scene = Entry?.IconKind == "scene"; var folder = Entry?.IconKind == "folder";
            grid.Children.Add(new Path { Data = Geometry.Parse(scene ? "M8,10 L56,10 L56,48 L8,48 Z M8,21 L56,21 M17,10 L25,21 M34,10 L42,21 M51,10 L56,17 M27,27 L40,34 L27,41 Z" : folder ? "M3,12 L23,12 L30,19 L61,19 L61,49 L3,49 Z" : "M16,4 L40,4 L52,16 L52,52 L16,52 Z M40,4 L40,16 L52,16 M23,28 L45,28 M23,36 L45,36 M23,44 L39,44"), Stroke = Brush.Parse(scene ? "#9BC5B7" : folder ? "#B9AC85" : "#9BA7B7"), StrokeThickness = 2, Fill = Brush.Parse("#303840") });
        }
        Content = new Viewbox { Child = grid, Stretch = Stretch.Uniform };
    }
}
