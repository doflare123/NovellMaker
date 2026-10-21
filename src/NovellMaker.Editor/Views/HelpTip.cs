using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NovellMaker.Editor.Views;

public sealed class HelpTip : Button
{
    public static readonly StyledProperty<string> ExplanationProperty = AvaloniaProperty.Register<HelpTip, string>(nameof(Explanation), "");
    public string Explanation { get => GetValue(ExplanationProperty); set => SetValue(ExplanationProperty, value); }
    public HelpTip()
    {
        Content = "?"; Width = 22; Height = 22; Padding = new Thickness(0); Background = Brushes.Transparent;
        Foreground = Brush.Parse("#A7B5C7"); HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(this, "Справка");
        PropertyChanged += (_, e) => { if (e.Property == ExplanationProperty) ToolTip.SetTip(this, new TextBlock { Text = Explanation, MaxWidth = 340, TextWrapping = TextWrapping.Wrap }); };
        Click += (_, _) => ToolTip.SetIsOpen(this, !ToolTip.GetIsOpen(this));
    }
}
