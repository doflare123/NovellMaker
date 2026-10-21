using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

public sealed class ScreenLibraryView : UserControl
{
    private EditorSession? session;
    private readonly ScreenDesignSurface surface = new() { Name = "ScreenDesigner" };
    public ScreenLibraryView()
    {
        var root = new DockPanel();
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 5, LineSpacing = 5, Margin = new Thickness(8) };
        void Button(string name, string label, Action action)
        { var b = new Button { Name = name, Content = label }; b.Click += (_, _) => action(); tools.Children.Add(b); }
        Button("ScreenSettings", "Параметры", () => { if (session?.SelectedScreen is { } s) session.SelectScreenNode(s.Nodes.Single(n => n.ParentId is null).Id); });
        tools.Children.Add(new HelpTip { Explanation = "Перетащите компонент из панели «Компоненты». Выделите элемент на экране; его свойства находятся справа. Угол рамки изменяет размер. Escape отменяет перетаскивание; Ctrl+Z отменяет изменение." });
        var zoom = new ComboBox { Name = "ScreenZoom", ItemsSource = new[] { "Вписать", "50%", "75%", "100%", "150%", "200%" }, SelectedIndex = 0, MinWidth = 90 };
        zoom.SelectionChanged += (_, _) => surface.SetZoom(zoom.SelectedIndex switch { 1 => .5, 2 => .75, 3 => 1, 4 => 1.5, 5 => 2, _ => null });
        tools.Children.Add(zoom);
        var bar = new Border { Background = Brush.Parse("#272B31"), BorderBrush = Brush.Parse("#3A3F47"), BorderThickness = new Thickness(0, 0, 0, 1), Child = tools };
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var error = new TextBlock { Name = "ComponentAddError", Foreground = Brush.Parse("#E2B684"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0) };
        error.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(EditorSession.ScreenAuthoringError)));
        DockPanel.SetDock(error, Dock.Top); root.Children.Add(error); root.Children.Add(surface); Content = root;
        DataContextChanged += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; surface.DataContext = session; Subscribe(); Refresh(); };
        AttachedToVisualTree += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); }; DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private void Subscribe() { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } }
    private void Unsubscribe() { if (session is not null) session.PropertyChanged -= Changed; }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(EditorSession.SelectedScreen) or nameof(EditorSession.CanEdit)) Refresh(); }
    private void Refresh()
    {
        IsEnabled = session?.CanEdit == true;
    }
}
