using System.Collections.Immutable;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Scenes;

/// <summary>
/// One authoring resource: visual components, dialogue sequence and scenario/event bindings.
/// </summary>
public sealed record NovelScene(Guid Id, string Name)
{
    public Guid Id { get; init; } = ValidateId(Id);

    public string Name { get; init; } = ValidateName(Name);

    public ImmutableArray<DialogueLine> Lines { get; init; } = [];

    public SceneCanvas Canvas { get; init; } = SceneCanvas.Empty(Id);

    public NovellMaker.Core.Screens.ScreenAsset AsScreen() => Canvas.AsScreen(Id, Name);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPresentation => BackgroundImageId is not null || Canvas.HandlerScriptId is not null || Canvas.Nodes.Length > 1 ||
        Canvas.Nodes.Any(n => n.Kind != NovellMaker.Core.Screens.ScreenNodeKind.Container || n.ImageId is not null);

    public ImmutableArray<ScenarioAction> Actions { get; init; } = [];
    public ImmutableArray<ScriptBlock> ScriptBlocks { get; init; } = [];
    public bool UseActions { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasActionScript => HasLegacyActionScript || !ScriptBlocks.IsEmpty;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLegacyActionScript => UseActions || !Actions.IsEmpty;

    public string? DialogueCssOverride { get; init; }

    public Guid? BackgroundImageId { get; init; }

    public static NovelScene Create(string name) => new(Guid.NewGuid(), name);

    public bool ContentEquals(NovelScene? other)
    {
        if (other is null ||
            Id != other.Id ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
            !string.Equals(DialogueCssOverride, other.DialogueCssOverride, StringComparison.Ordinal) ||
            BackgroundImageId != other.BackgroundImageId ||
            Canvas is null || other.Canvas is null || !AsScreen().ContentEquals(other.AsScreen()) ||
            UseActions != other.UseActions ||
            Lines.IsDefault != other.Lines.IsDefault ||
            !System.Text.Json.JsonSerializer.Serialize(ScriptBlocks).Equals(System.Text.Json.JsonSerializer.Serialize(other.ScriptBlocks), StringComparison.Ordinal) ||
            !System.Text.Json.JsonSerializer.Serialize(Actions).Equals(System.Text.Json.JsonSerializer.Serialize(other.Actions), StringComparison.Ordinal))
        {
            return false;
        }

        if (Lines.IsDefault)
        {
            return true;
        }

        if (Lines.Length != other.Lines.Length)
        {
            return false;
        }

        for (var lineIndex = 0; lineIndex < Lines.Length; lineIndex++)
        {
            if (Lines[lineIndex] is null || !Lines[lineIndex].ContentEquals(other.Lines[lineIndex]))
            {
                return false;
            }
        }

        return true;
    }

    private static Guid ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор сцены не может быть пустым.", nameof(id));
        }

        return id;
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Название сцены не может быть пустым.", nameof(name));
        }

        return name;
    }
}
