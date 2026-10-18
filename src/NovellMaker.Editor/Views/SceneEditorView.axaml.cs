using Avalonia.Controls;
using Avalonia.Interactivity;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Editor.Views;

public sealed partial class SceneEditorView : UserControl
{
    public SceneEditorView() => InitializeComponent();
    private void Place(double x) { if (DataContext is SceneDocumentViewModel vm) vm.SetPlacement(new CharacterPlacement(x, 1, (double)vm.PlacementScale / 100, (int)vm.PlacementLayer)); }
    private void PlaceLeft(object? sender, RoutedEventArgs e) => Place(0.25);
    private void PlaceCenter(object? sender, RoutedEventArgs e) => Place(0.5);
    private void PlaceRight(object? sender, RoutedEventArgs e) => Place(0.75);
    private void PlaceAutomatic(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) vm.SetPlacement(null); }
    private async void RemovePlacement(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) await vm.ConfirmRemovePlacementAsync(); }
}
