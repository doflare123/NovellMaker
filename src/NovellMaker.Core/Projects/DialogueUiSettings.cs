using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Projects;

public sealed record DialogueUiSettings
{
    public string Css { get; init; } = DialogueCssParser.DefaultCss;

    public bool ShowVoiceProgress { get; init; } = true;

    public bool ShowVoiceControls { get; init; } = true;

    public bool ShowNavigationControls { get; init; } = true;
}
