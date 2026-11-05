using Avalonia.Controls;
using Avalonia.Interactivity;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Core.Scenes;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NovellMaker.Core.Audio;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.Views;

public sealed partial class SceneEditorView : UserControl
{
    private readonly VoiceAudition audition = new(new NovellMaker.Runtime.Audio.MonoGameAudioBackend());
    private readonly DispatcherTimer auditionTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private SceneDocumentViewModel? observedDocument;
    public SceneEditorView()
    {
        InitializeComponent();
        auditionTimer.Tick += (_, _) =>
        {
            try { Microsoft.Xna.Framework.FrameworkDispatcher.Update(); audition.Update(); if (!audition.IsPlaying) StopAudition(); }
            catch (Exception error) when (error is not OutOfMemoryException) { StopAudition(); VoiceAuditionStatus.Text = "Ошибка прослушивания: " + error.Message; }
        };
        DataContextChanged += (_, _) => ObserveDocument();
        AttachedToVisualTree += (_, _) => ObserveDocument();
        DetachedFromVisualTree += (_, _) => { StopAudition(); UnobserveDocument(); };
        DialogueLines.AddHandler(InputElement.GotFocusEvent, (_, e) => SelectRow(e.Source));
        DialogueLines.AddHandler(InputElement.KeyDownEvent, DialogueRowKeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(DialogueLines, true);
        DialogueLines.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is null) return;
            e.DragEffects = WavDrop(e) is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        });
        DialogueLines.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (WavDrop(e) is not { } drop || DataContext is not SceneDocumentViewModel vm) return;
            e.Handled = true; StopAudition();
            await vm.Session.AttachLineVoiceAsync(vm.Id, drop.Row.Id, vm.EditingLanguage, drop.Path);
        });
        ScriptDragDrop.Destination(this, () => (DataContext as SceneDocumentViewModel)?.Session, e =>
        {
            if (DataContext is not SceneDocumentViewModel scene) return null;
            var line = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<DialogueLineViewModel>().FirstOrDefault();
            return new(scene.Id, line?.Id ?? scene.SelectedLine?.Id, line is not null || scene.SelectedLine is not null ? ScriptPhase.DuringLine : ScriptPhase.SceneStart);
        });
    }
    private void ObserveDocument()
    {
        StopAudition();
        UnobserveDocument();
        observedDocument = DataContext as SceneDocumentViewModel;
        if (observedDocument is not null) { observedDocument.PropertyChanged += DocumentChanged; observedDocument.Session.PropertyChanged += SessionChanged; }
    }
    private void UnobserveDocument()
    {
        if (observedDocument is not null) { observedDocument.PropertyChanged -= DocumentChanged; observedDocument.Session.PropertyChanged -= SessionChanged; }
        observedDocument = null;
    }
    private void SessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(EditorSession.IsBusy) && observedDocument?.Session.IsBusy == true) StopAudition(); }
    private void DocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SceneDocumentViewModel.SelectedLine) or nameof(SceneDocumentViewModel.EditingLanguage) or nameof(SceneDocumentViewModel.SelectedSection)) StopAudition();
    }
    private static DialogueLineViewModel? Row(object? source) => (source as Control)?.GetSelfAndVisualAncestors().OfType<Control>()
        .Select(c => c.DataContext).OfType<DialogueLineViewModel>().FirstOrDefault();
    private void SelectRow(object? source) { if (DataContext is SceneDocumentViewModel vm && Row(source) is { } row) vm.SelectedLine = row; }
    private (DialogueLineViewModel Row, string Path)? WavDrop(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToArray();
        if (DataContext is not SceneDocumentViewModel { Session.CanEdit: true, IsEditingLanguageValid: true } || Row(e.Source) is not { } row || files?.Length != 1) return null;
        var path = files[0].TryGetLocalPath();
        return path is not null && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) ? (row, path) : null;
    }
    private void DialogueRowFocused(object? sender, RoutedEventArgs e) => SelectRow(sender);
    private void DialogueRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Control || DataContext is not SceneDocumentViewModel { Session.CanEdit: true } vm) return;
        if (Row(e.Source) is null) return;
        e.Handled = true; SelectRow(e.Source); vm.MissingVoicesOnly = false; vm.SpeakerFilter = null; vm.AddLine();
        var id = vm.SelectedLine?.Id;
        if (vm.SelectedLine is { } selected) DialogueLines.ScrollIntoView(selected);
        Dispatcher.UIThread.Post(() =>
        {
            var box = DialogueLines.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(c => c.Name == "RowTextBox" && (c.DataContext as DialogueLineViewModel)?.Id == id);
            box?.Focus();
        }, DispatcherPriority.Loaded);
    }
    private async void PasteDialogue(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SceneDocumentViewModel { Session.CanEdit: true, IsEditingLanguageValid: true } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new DialoguePasteWindow(vm);
        if (await dialog.ShowDialog<IReadOnlyList<DialoguePasteLine>?>(owner) is { } lines)
        {
            try { vm.InsertDialogue(lines); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { VoiceAuditionStatus.Text = error.Message; }
        }
    }
    private async void AttachRowVoice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SceneDocumentViewModel vm || Row(sender) is not { } row) return;
        StopAudition(); vm.SelectedLine = row;
        await vm.Session.AttachLineVoiceAsync(vm.Id, row.Id, vm.EditingLanguage);
    }
    private async void ExportRecordingSheet(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) await vm.Session.ExportRecordingSheetAsync(vm.Id, vm.EditingLanguage); }
    private async void ImportRecordingSheet(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) { StopAudition(); await vm.Session.ImportRecordingSheetAsync(vm.Id); } }
    private void PlayRowVoice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SceneDocumentViewModel vm || Row(sender) is not { VoiceId: { } id } row || vm.Session.Project is not { } project || vm.Session.ProjectDirectory is not { } root) return;
        vm.SelectedLine = row; StopAudition();
        try
        {
            var asset = project.Audio.Assets.Single(a => a.Id == id);
            var master = project.Audio.Mixer.GetValueOrDefault(AudioGroup.Master) ?? new(); var voice = project.Audio.Mixer.GetValueOrDefault(AudioGroup.Voice) ?? new();
            audition.Play(AudioAssetPath.Resolve(root, asset.RelativePath), master.Muted || voice.Muted ? 0 : master.Volume * voice.Volume);
            VoiceAuditionStatus.Text = "Прослушивание: " + row.VoiceSummary; auditionTimer.Start();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { StopAudition(); VoiceAuditionStatus.Text = "Ошибка прослушивания: " + error.Message; }
    }
    private void StopAudition()
    {
        auditionTimer.Stop();
        try { audition.Stop(); VoiceAuditionStatus.Text = ""; }
        catch (Exception error) when (error is not OutOfMemoryException) { VoiceAuditionStatus.Text = "Ошибка остановки записи: " + error.Message; }
    }
    private void StopVoiceAudition(object? sender, RoutedEventArgs e) => StopAudition();
    private async void ImportAudio(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) await vm.Session.ImportAudioAsync(); }
    private void DuplicateSceneClicked(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) vm.Session.DuplicateScreen(); }
    private void Place(double x) { if (DataContext is SceneDocumentViewModel vm) vm.SetPlacement(new CharacterPlacement(x, 1, (double)vm.PlacementScale / 100, (int)vm.PlacementLayer)); }
    private void PlaceLeft(object? sender, RoutedEventArgs e) => Place(0.25);
    private void PlaceCenter(object? sender, RoutedEventArgs e) => Place(0.5);
    private void PlaceRight(object? sender, RoutedEventArgs e) => Place(0.75);
    private void PlaceAutomatic(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) vm.SetPlacement(null); }
    private async void RemovePlacement(object? sender, RoutedEventArgs e) { if (DataContext is SceneDocumentViewModel vm) await vm.ConfirmRemovePlacementAsync(); }
}
