using Avalonia.Controls;
using Avalonia.Interactivity;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Core.Scenes;
using Avalonia.VisualTree;

namespace NovellMaker.Editor.Views;

public sealed partial class SceneEditorView : UserControl
{
    public SceneEditorView()
    {
        InitializeComponent();
        ScriptDragDrop.Destination(this, () => (DataContext as SceneDocumentViewModel)?.Session, e =>
        {
            if (DataContext is not SceneDocumentViewModel scene) return null;
            var line = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<DialogueLineViewModel>().FirstOrDefault();
            return new(scene.Id, line?.Id ?? scene.SelectedLine?.Id, line is not null || scene.SelectedLine is not null ? ScriptPhase.DuringLine : ScriptPhase.SceneStart);
        });
    }
    private async void ImportAudio(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) await vm.Session.ImportAudioAsync(); }
    private void Place(double x) { if (DataContext is SceneDocumentViewModel vm) vm.SetPlacement(new CharacterPlacement(x, 1, (double)vm.PlacementScale / 100, (int)vm.PlacementLayer)); }
    private void PlaceLeft(object? sender, RoutedEventArgs e) => Place(0.25);
    private void PlaceCenter(object? sender, RoutedEventArgs e) => Place(0.5);
    private void PlaceRight(object? sender, RoutedEventArgs e) => Place(0.75);
    private void PlaceAutomatic(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) vm.SetPlacement(null); }
    private async void RemovePlacement(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) await vm.ConfirmRemovePlacementAsync(); }
}
