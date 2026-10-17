using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NovellMaker.Core.Styling;

namespace NovellMaker.Editor.Views;

/// <summary>A design-time sample of a dialogue skin, not a script interpreter.</summary>
public sealed partial class DialogueStyleSample : UserControl
{
    public static readonly StyledProperty<DialogueStyle?> StyleSheetProperty = AvaloniaProperty.Register<DialogueStyleSample, DialogueStyle?>(nameof(StyleSheet));
    public static readonly StyledProperty<string> SampleTextProperty = AvaloniaProperty.Register<DialogueStyleSample, string>(nameof(SampleText), "Текст реплики");
    public static readonly StyledProperty<bool> ShowVoiceProgressProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(ShowVoiceProgress), true);
    public static readonly StyledProperty<bool> ShowVoiceControlsProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(ShowVoiceControls), true);
    public static readonly StyledProperty<bool> ShowNavigationControlsProperty = AvaloniaProperty.Register<DialogueStyleSample, bool>(nameof(ShowNavigationControls), true);
    public static readonly StyledProperty<ICommand?> PreviousCommandProperty = AvaloniaProperty.Register<DialogueStyleSample, ICommand?>(nameof(PreviousCommand));
    public static readonly StyledProperty<ICommand?> NextCommandProperty = AvaloniaProperty.Register<DialogueStyleSample, ICommand?>(nameof(NextCommand));
    private readonly DispatcherTimer timer;
    private readonly Stopwatch watch = new();
    private bool ready;

    public DialogueStyle? StyleSheet { get => GetValue(StyleSheetProperty); set => SetValue(StyleSheetProperty, value); }
    public string SampleText { get => GetValue(SampleTextProperty); set => SetValue(SampleTextProperty, value); }
    public bool ShowVoiceProgress { get => GetValue(ShowVoiceProgressProperty); set => SetValue(ShowVoiceProgressProperty, value); }
    public bool ShowVoiceControls { get => GetValue(ShowVoiceControlsProperty); set => SetValue(ShowVoiceControlsProperty, value); }
    public bool ShowNavigationControls { get => GetValue(ShowNavigationControlsProperty); set => SetValue(ShowNavigationControlsProperty, value); }
    public ICommand? PreviousCommand { get => GetValue(PreviousCommandProperty); set => SetValue(PreviousCommandProperty, value); }
    public ICommand? NextCommand { get => GetValue(NextCommandProperty); set => SetValue(NextCommandProperty, value); }

    public DialogueStyleSample()
    {
        InitializeComponent();
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (ready && change.Property == StyleSheetProperty) ApplyStyle();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
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
