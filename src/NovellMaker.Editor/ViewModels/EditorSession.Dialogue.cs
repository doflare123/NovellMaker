using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;
using NovellMaker.Rendering;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public ScreenDefinition? ScreenPresentation => SelectedScreen is { } screen ? PresentScreen(screen) : null;

    internal ScreenDefinition PresentScreen(ScreenAsset screen)
    {
        var source = screen.Definition();
        if (!screen.Nodes.Any(n => n.Dialogue != DialogueBinding.None)) return source;
        var style = ActiveScene?.Style ?? DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
        var frame = ActiveScene?.SampleFrame;
        var text = frame?.LineId is null ? "Текст реплики" : ActiveScene?.SampleText ?? "";
        var speaker = frame?.LineId is null ? "Имя говорящего" : frame.SpeakerName;
        var metrics = DialogueRasterizer.Measure(style, speaker, text);
        return DialogueCanvas.Present(source, style, speaker, text, true, metrics.Height, metrics.SpeakerHeight);
    }

    public void AddDialogueComponent()
    {
        if (!CanEdit || SelectedScreen is not { } screen) return;
        try
        {
            var nodes = DialogueCanvas.Add(screen.Nodes, screen.Id, ActiveScene?.Style ?? DialogueCssParser.Parse(DialogueCssParser.DefaultCss), legacy: true);
            UpdateScreen(screen with { Nodes = nodes }); SelectScreenNode(nodes[^3].Id);
        }
        catch (ArgumentException error) { ReportScreenError("Диалог не добавлен: " + error.Message); }
    }

    public void UseCanvasDialogueLayout()
    {
        if (SelectedScreen is not { } screen || ScreenPresentation is not { } presentation) return;
        UpdateScreen(screen with { Nodes = DialogueCanvas.Materialize(screen.Definition(), presentation) });
    }

    public void SetDialogueStyleProperty(string property, string value)
    {
        if (!CanEdit || project is null || ActiveScene is not { } active) return;
        if (property is not ("font-size" or "color" or "background-color" or "border-color" or "border-width" or "border-radius" or "font-family" or "text-align"))
            throw new ArgumentException("Неизвестное свойство оформления.");
        var scene = project.Scenes.Single(s => s.Id == active.Id);
        var css = scene.DialogueCssOverride ?? project.DialogueUi.Css;
        // Preserve comments and keyframes. Work on a same-length masked copy to locate only
        // a declaration in .dialogue, rather than matching property-like text in a comment.
        _ = DialogueCssParser.Parse(css);
        var masked = System.Text.RegularExpressions.Regex.Replace(css, @"/\*[\s\S]*?\*/", m => new string(' ', m.Length));
        var selector = System.Text.RegularExpressions.Regex.Match(masked, @"\.dialogue\s*\{");
        var opening = selector.Index + selector.Length - 1;
        var end = masked.IndexOf('}', opening);
        var body = masked.Substring(opening + 1, end - opening - 1);
        var match = System.Text.RegularExpressions.Regex.Match(body, @"(?m)(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(property) + @"\s*:\s*[^;{}]+;?");
        if (match.Success)
            css = css.Remove(opening + 1 + match.Index, match.Length).Insert(opening + 1 + match.Index, property + ": " + value + ";");
        else
            css = css.Insert(opening + 1, "\n  " + property + ": " + value + ";");
        _ = DialogueCssParser.Parse(css);
        UpdateScene(scene with { DialogueCssOverride = css }, "dialogue-style:" + scene.Id + ":" + property);
    }
}
