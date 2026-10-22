using Avalonia.Input;
using System.ComponentModel;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Core.Scenes;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NovellMaker.Core.Styling;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Text;
using Avalonia.Controls.Documents;

namespace NovellMaker.Editor.Views;

/// <summary>A design-time sample of a dialogue skin, not a script interpreter.</summary>
public sealed partial class DialogueStyleSample : UserControl
{
    public static readonly StyledProperty<bool> CanvasOnlyProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(CanvasOnly));
    public bool CanvasOnly { get => GetValue(CanvasOnlyProperty); set => SetValue(CanvasOnlyProperty, value); }
    public static readonly StyledProperty<DialogueStyle?> StyleSheetProperty = AvaloniaProperty.Register<DialogueStyleSample, DialogueStyle?>(nameof(StyleSheet));
    public static readonly StyledProperty<string> SampleTextProperty = AvaloniaProperty.Register<DialogueStyleSample, string>(nameof(SampleText), "Текст реплики");
    public static readonly StyledProperty<ImageAsset?> BackgroundImageProperty = AvaloniaProperty.Register<DialogueStyleSample, ImageAsset?>(nameof(BackgroundImage));
    public static readonly StyledProperty<string?> ProjectDirectoryProperty = AvaloniaProperty.Register<DialogueStyleSample, string?>(nameof(ProjectDirectory));
    public static readonly StyledProperty<int> AssetRevisionProperty = AvaloniaProperty.Register<DialogueStyleSample, int>(nameof(AssetRevision));
    public static readonly StyledProperty<SceneFrame?> SampleFrameProperty = AvaloniaProperty.Register<DialogueStyleSample, SceneFrame?>(nameof(SampleFrame));
    public static readonly StyledProperty<bool> ShowVoiceProgressProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(ShowVoiceProgress), true);
    public static readonly StyledProperty<bool> ShowVoiceControlsProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(ShowVoiceControls), true);
    public static readonly StyledProperty<bool> ShowNavigationControlsProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(ShowNavigationControls), true);
    public static readonly StyledProperty<ICommand?> PreviousCommandProperty = AvaloniaProperty.Register<DialogueStyleSample, ICommand?>(nameof(PreviousCommand));
    public static readonly StyledProperty<ICommand?> NextCommandProperty = AvaloniaProperty.Register<DialogueStyleSample, ICommand?>(nameof(NextCommand));
    public static readonly StyledProperty<SceneDocumentViewModel?> PlacementEditorProperty = AvaloniaProperty.Register<DialogueStyleSample, SceneDocumentViewModel?>(nameof(PlacementEditor));
    public SceneDocumentViewModel? PlacementEditor { get => GetValue(PlacementEditorProperty); set => SetValue(PlacementEditorProperty, value); }
    private readonly Border selectionOutline = new() { BorderBrush = Brushes.LightSteelBlue, BorderThickness = new Thickness(2), IsHitTestVisible = false, ZIndex = int.MaxValue };
    private ProjectImageView? draggedView;
    private IPointer? dragPointer;
    private Point dragStart;
    private Point dragOrigin;
    private Guid? dragLineId;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch watch = new();
    private bool ready;

    public DialogueStyle? StyleSheet { get => GetValue(StyleSheetProperty); set => SetValue(StyleSheetProperty, value); }
    public string SampleText { get => GetValue(SampleTextProperty); set => SetValue(SampleTextProperty, value); }
    public ImageAsset? BackgroundImage { get => GetValue(BackgroundImageProperty); set => SetValue(BackgroundImageProperty, value); }
    public string? ProjectDirectory { get => GetValue(ProjectDirectoryProperty); set => SetValue(ProjectDirectoryProperty, value); }
    public int AssetRevision { get => GetValue(AssetRevisionProperty); set => SetValue(AssetRevisionProperty, value); }
    public SceneFrame? SampleFrame { get => GetValue(SampleFrameProperty); set => SetValue(SampleFrameProperty, value); }
    public bool ShowVoiceProgress { get => GetValue(ShowVoiceProgressProperty); set => SetValue(ShowVoiceProgressProperty, value); }
    public bool ShowVoiceControls { get => GetValue(ShowVoiceControlsProperty); set => SetValue(ShowVoiceControlsProperty, value); }
    public bool ShowNavigationControls { get => GetValue(ShowNavigationControlsProperty); set => SetValue(ShowNavigationControlsProperty, value); }
    public ICommand? PreviousCommand { get => GetValue(PreviousCommandProperty); set => SetValue(PreviousCommandProperty, value); }
    public ICommand? NextCommand { get => GetValue(NextCommandProperty); set => SetValue(NextCommandProperty, value); }

    public DialogueStyleSample()
    {
        InitializeComponent();
        CharacterLayer.Children.Add(selectionOutline);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && draggedView is not null) { CancelDrag(); e.Handled = true; } };
        CharacterLayer.PointerMoved += DragMoved;
        CharacterLayer.PointerReleased += DragReleased;
        CharacterLayer.PointerCaptureLost += (_, _) => CancelDrag();
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible) { StopAnimation(); return; }
            SampleAt(watch.Elapsed);
            if (StyleSheet is { } sheet && (sheet.AnimationDuration <= TimeSpan.Zero ||
                sheet.AnimationIterationCount is { } count && watch.Elapsed.TotalSeconds >= sheet.AnimationDuration.TotalSeconds * count))
                timer.Stop();
        };
        ready = true;
        ApplyStyle();
        ApplyText();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (ready && change.Property == CanvasOnlyProperty) { AnimationTools.IsVisible = !CanvasOnly; ApplyText(); }
        if (ready && change.Property == PlacementEditorProperty)
        {
            if (change.OldValue is SceneDocumentViewModel old) old.PropertyChanged -= PlacementChanged;
            if (PlacementEditor is { } editor) editor.PropertyChanged += PlacementChanged;
            UpdateSelectionOutline();
        }
        if (ready && change.Property == SampleFrameProperty) CancelDrag();
        if (ready && change.Property == StyleSheetProperty) ApplyStyle();
        if (ready && (change.Property == SampleTextProperty || change.Property == SampleFrameProperty)) ApplyText();
        if (ready && (change.Property == SampleFrameProperty || change.Property == ProjectDirectoryProperty || change.Property == AssetRevisionProperty)) ApplyCharacters();
        if (ready && (change.Property == BackgroundImageProperty || change.Property == ProjectDirectoryProperty || change.Property == AssetRevisionProperty))
        {
            SceneBackground.Asset = BackgroundImage;
            SceneBackground.ProjectDirectory = ProjectDirectory;
            SceneBackground.Revision = AssetRevision;
            SceneBackground.IsVisible = BackgroundImage is not null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        if (PlacementEditor is { } editor)
        {
            editor.PropertyChanged -= PlacementChanged;
            editor.PropertyChanged += PlacementChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        CancelDrag();
        if (PlacementEditor is { } editor) editor.PropertyChanged -= PlacementChanged;
        timer.Stop();
        watch.Stop();
        base.OnDetachedFromVisualTree(args);
    }

    private void ApplyStyle()
    {
        StopAnimation();
        var sheet = StyleSheet;
        PlayStyleAnimationButton.IsEnabled = sheet?.HasAnimation == true;
        if (sheet is null) return;
        DialoguePlate.Background = CssBrush(sheet.BackgroundColor);
        DialoguePlate.BorderBrush = CssBrush(sheet.BorderColor);
        DialoguePlate.BorderThickness = new Thickness(sheet.BorderWidth);
        DialoguePlate.CornerRadius = new CornerRadius(sheet.BorderRadius);
        DialoguePlate.Padding = new Thickness(sheet.PaddingLeft, sheet.PaddingTop, sheet.PaddingRight, sheet.PaddingBottom);
        DialoguePlate.Width = sheet.Width;
        DialoguePlate.MinHeight = sheet.MinHeight;
        Canvas.SetLeft(DialoguePlate, sheet.Left);
        Canvas.SetBottom(DialoguePlate, sheet.Bottom);
        DialogueText.Foreground = CssBrush(sheet.TextColor);
        DialogueText.FontSize = sheet.FontSize;
        DialogueText.FontFamily = new FontFamily(sheet.FontFamily);
        DialogueText.TextAlignment = sheet.TextAlign switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left };
        DialogueSpeaker.Foreground = CssBrush(sheet.TextColor);
        DialogueSpeaker.FontSize = sheet.FontSize;
        DialogueSpeaker.FontFamily = DialogueText.FontFamily;
    }

    private void ApplyText()
    {
        DialoguePlate.IsVisible = !CanvasOnly;
        var runs = NovellMaker.Core.Text.DialogueText.Parse(SampleText);
        DialogueText.Inlines?.Clear();
        if (runs.Count == 1 && runs[0].Color is null) DialogueText.Text = runs[0].Text;
        else
        {
            DialogueText.Text = null;
            DialogueText.Inlines ??= [];
            foreach (var run in runs)
            {
                var inline = new Run(run.Text);
                if (run.Color is not null) inline.Foreground = CssBrush(run.Color);
                DialogueText.Inlines.Add(inline);
            }
        }
        DialogueSpeaker.Text = SampleFrame?.SpeakerName ?? "";
        DialogueSpeaker.IsVisible = !string.IsNullOrEmpty(DialogueSpeaker.Text);
    }

    private void ApplyCharacters()
    {
        var sprites = SampleFrame?.Characters ?? [];
        // Reuse existing views so typing dialogue does not restart every image load.
        var existing = CharacterLayer.Children.OfType<ProjectImageView>().ToDictionary(view => (Guid)view.Tag!);
        var ids = sprites.Select(sprite => sprite.CharacterId).ToHashSet();
        foreach (var old in existing.Where(pair => !ids.Contains(pair.Key))) CharacterLayer.Children.Remove(old.Value);
        for (var index = 0; index < sprites.Length; index++)
        {
            var sprite = sprites[index];
            if (!existing.TryGetValue(sprite.CharacterId, out var view))
            {
                view = new ProjectImageView { Tag = sprite.CharacterId, TransparentBackground = true, Focusable = true };
                view.PointerPressed += DragPressed;
                CharacterLayer.Children.Add(view);
            }
            view.Asset = sprite.Image;
            var bounds = CharacterLayout.Calculate(sprite.Image.PixelWidth, sprite.Image.PixelHeight, sprite.AutomaticSlot, sprites.Length, sprite.Placement);
            view.Width = bounds.Width;
            view.Height = bounds.Height;
            view.ProjectDirectory = ProjectDirectory;
            view.Revision = AssetRevision;
            Canvas.SetLeft(view, bounds.X);
            Canvas.SetTop(view, bounds.Y);
            view.ZIndex = index;
            view.Cursor = PlacementEditor is null ? null : new Cursor(StandardCursorType.Hand);
        }
        UpdateSelectionOutline();
    }

    private void PlacementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SceneDocumentViewModel.SelectedPlacementCharacter) or nameof(SceneDocumentViewModel.CanPlaceCharacter)) UpdateSelectionOutline();
    }

    private void UpdateSelectionOutline()
    {
        var id = PlacementEditor?.SelectedPlacementCharacter?.Id;
        var view = CharacterLayer.Children.OfType<ProjectImageView>().FirstOrDefault(item => (Guid)item.Tag! == id);
        selectionOutline.IsVisible = view is not null && PlacementEditor?.CanPlaceCharacter == true;
        if (view is null) return;
        selectionOutline.Width = view.Width;
        selectionOutline.Height = view.Height;
        Canvas.SetLeft(selectionOutline, Canvas.GetLeft(view));
        Canvas.SetTop(selectionOutline, Canvas.GetTop(view));
    }

    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PlacementEditor is not { CanEditLine: true } editor || sender is not ProjectImageView view || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
        editor.SelectedPlacementCharacter = editor.PlacementOptions.FirstOrDefault(option => option.Id == (Guid)view.Tag!);
        if (!editor.CanPlaceCharacter) return;
        editor.Session.EndEditGroup();
        view.Focus();
        draggedView = view;
        dragLineId = editor.SelectedLine?.Id;
        dragStart = e.GetPosition(CharacterLayer);
        dragOrigin = new(Canvas.GetLeft(view), Canvas.GetTop(view));
        dragPointer = e.Pointer;
        e.Pointer.Capture(CharacterLayer);
        e.Handled = true;
    }

    private void DragMoved(object? sender, PointerEventArgs e)
    {
        if (draggedView is not { } view) return;
        var delta = e.GetPosition(CharacterLayer) - dragStart;
        Canvas.SetLeft(view, Math.Clamp(dragOrigin.X + delta.X + view.Width / 2, -1280, 2560) - view.Width / 2);
        Canvas.SetTop(view, Math.Clamp(dragOrigin.Y + delta.Y + view.Height, -720, 1440) - view.Height);
        UpdateSelectionOutline();
        e.Handled = true;
    }

    private void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedView is not { } view || PlacementEditor is not { } editor) return;
        var x = (Canvas.GetLeft(view) + view.Width / 2) / CharacterLayout.Width;
        var y = (Canvas.GetTop(view) + view.Height) / CharacterLayout.Height;
        var delta = e.GetPosition(CharacterLayer) - dragStart;
        var moved = Math.Abs(delta.X) + Math.Abs(delta.Y) > 0.5;
        var valid = dragLineId == editor.SelectedLine?.Id;
        // Free capture before committing: the resulting frame refresh reuses these image views.
        draggedView = null;
        dragPointer?.Capture(null);
        dragPointer = null;
        if (moved && valid) editor.SetPlacement(new(x, y, (double)editor.PlacementScale / 100, (int)editor.PlacementLayer));
        else ApplyCharacters();
        editor.Session.EndEditGroup();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (draggedView is not { } view) return;
        Canvas.SetLeft(view, dragOrigin.X);
        Canvas.SetTop(view, dragOrigin.Y);
        draggedView = null;
        dragPointer?.Capture(null);
        dragPointer = null;
        UpdateSelectionOutline();
    }

    public void SampleAt(TimeSpan elapsed)
    {
        if (StyleSheet is not { } sheet) return;
        var sample = sheet.Sample(elapsed);
        DialoguePlate.Opacity = sample.Opacity;
        DialoguePlate.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(sample.Scale, sample.Scale), new TranslateTransform(sample.TranslateX, sample.TranslateY) }
        };
    }

    private void PlayClicked(object? sender, RoutedEventArgs args)
    {
        if (StyleSheet?.HasAnimation != true) return;
        watch.Restart();
        SampleAt(TimeSpan.Zero);
        timer.Start();
    }

    private void StopClicked(object? sender, RoutedEventArgs args) => StopAnimation();

    private void StopAnimation()
    {
        timer.Stop();
        watch.Reset();
        DialoguePlate.Opacity = 1;
        DialoguePlate.RenderTransform = null;
    }

    private static SolidColorBrush CssBrush(string color)
    {
        // CSS stores alpha last; Avalonia uses alpha first.
        if (color.Length == 9) color = "#" + color[7..9] + color[1..7];
        return new SolidColorBrush(Color.Parse(color));
    }
}
