using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Projects;

internal static partial class ProjectJsonCodec
{
    private sealed class VersionProbe
    {
        [JsonRequired]
        public int FormatVersion { get; init; }
    }

    private sealed class Version1ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }
    }

    private sealed class Version2ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version2SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version3ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version3SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public List<ImageDocument?>? Images { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version4ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version4SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public List<ImageDocument?>? Images { get; init; }

        [JsonRequired]
        public List<CharacterDocument?>? Characters { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version6ProjectDocument : Version5ProjectDocument
    {
        [JsonRequired]
        public bool ExplorerShowsFiles { get; init; }
    }

    private sealed class Version7ProjectDocument
    {
        [JsonRequired] public int FormatVersion { get; init; }
        [JsonRequired] public Guid Id { get; init; }
        [JsonRequired] public string? Name { get; init; }
        [JsonRequired] public List<Version7SceneDocument?>? Scenes { get; init; }
        [JsonRequired] public List<ImageDocument?>? Images { get; init; }
        [JsonRequired] public List<CharacterDocument?>? Characters { get; init; }
        [JsonRequired] public string? DefaultLanguage { get; init; }
        [JsonRequired] public bool ExplorerShowsFiles { get; init; }
        [JsonRequired] public DialogueUiDocument? DialogueUi { get; init; }
    }

    private class Version7SceneDocument : Version5SceneDocument
    {
        [JsonRequired] public ImmutableArray<ScenarioAction>? Actions { get; init; }
        [JsonRequired] public bool UseActions { get; init; }
    }

    private class Version8SceneDocument : Version7SceneDocument
    {
        [JsonRequired] public ImmutableArray<ScriptBlock>? ScriptBlocks { get; init; }
    }

    private class Version8ProjectDocument
    {
        [JsonRequired] public int FormatVersion { get; init; }
        [JsonRequired] public Guid Id { get; init; }
        [JsonRequired] public string? Name { get; init; }
        [JsonRequired] public List<Version8SceneDocument?>? Scenes { get; init; }
        [JsonRequired] public List<ImageDocument?>? Images { get; init; }
        [JsonRequired] public List<CharacterDocument?>? Characters { get; init; }
        [JsonRequired] public string? DefaultLanguage { get; init; }
        [JsonRequired] public bool ExplorerShowsFiles { get; init; }
        [JsonRequired] public DialogueUiDocument? DialogueUi { get; init; }
    }

    private class Version9ProjectDocument : Version8ProjectDocument
    {
        [JsonRequired] public NovellMaker.Core.Audio.AudioProject? Audio { get; init; }
    }

    private class Version10ProjectDocument : Version9ProjectDocument
    {
        [JsonRequired] public ImmutableArray<LuaScriptAsset>? Scripts { get; init; }
    }

    private sealed class Version11ProjectDocument : Version10ProjectDocument
    {
        [JsonRequired] public ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>? Screens { get; init; }
        [JsonRequired] public Guid? PauseScreenId { get; init; }
    }

    private sealed class Version13SceneDocument : Version8SceneDocument
    {
        [JsonRequired] public SceneCanvas? Canvas { get; init; }
    }

    private sealed class Version13ProjectDocument : Version10ProjectDocument
    {
        [JsonRequired] public new List<Version13SceneDocument?>? Scenes { get; init; }
        [JsonRequired] public Guid? PauseScreenId { get; init; }
    }

    private class Version14ProjectDocument : Version10ProjectDocument
    {
        [JsonRequired] public new List<Version13SceneDocument?>? Scenes { get; init; }
        [JsonRequired] public ImmutableArray<NovellMaker.Core.Screens.ProjectInputBinding>? InputBindings { get; init; }
    }

    private class Version17ProjectDocument : Version14ProjectDocument
    {
        [JsonRequired] public Guid? StartScreenSceneId { get; init; }
    }

    private sealed class Version20ProjectDocument : Version17ProjectDocument
    {
        [JsonRequired] public NovellMaker.Core.ExternalServices.ReleaseCheckSettings? ReleaseCheck { get; init; }
    }

    private class Version5ProjectDocument
    {
        [JsonRequired]
        public int FormatVersion { get; init; }

        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version5SceneDocument?>? Scenes { get; init; }

        [JsonRequired]
        public List<ImageDocument?>? Images { get; init; }

        [JsonRequired]
        public List<CharacterDocument?>? Characters { get; init; }

        [JsonRequired]
        public string? DefaultLanguage { get; init; }

        [JsonRequired]
        public DialogueUiDocument? DialogueUi { get; init; }
    }

    private sealed class Version2SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<LegacyLineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }
    }

    private sealed class Version3SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<LegacyLineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private sealed class Version4SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version4LineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private class Version5SceneDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<Version5LineDocument?>? Lines { get; init; }

        [JsonRequired]
        public string? DialogueCssOverride { get; init; }

        [JsonRequired]
        public Guid? BackgroundImageId { get; init; }
    }

    private sealed class LegacyLineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }
    }

    private sealed class Version4LineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }

        [JsonRequired]
        public Guid? SpeakerId { get; init; }

        [JsonRequired]
        public Guid? CharacterStateId { get; init; }

        [JsonRequired]
        public Guid? HideCharacterId { get; init; }
    }

    private sealed class Version5LineDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? TextKey { get; init; }

        [JsonRequired]
        public SortedDictionary<string, string?>? Texts { get; init; }

        [JsonRequired]
        public Guid? SpeakerId { get; init; }

        [JsonRequired]
        public Guid? CharacterStateId { get; init; }

        [JsonRequired]
        public Guid? HideCharacterId { get; init; }

        [JsonRequired]
        public List<PlacementCommandDocument?>? Placements { get; init; }
    }

    private sealed class PlacementCommandDocument
    {
        [JsonRequired] public Guid CharacterId { get; init; }
        [JsonRequired] public PlacementDocument? Placement { get; init; }
    }

    private sealed class PlacementDocument
    {
        [JsonRequired] public double X { get; init; }
        [JsonRequired] public double Y { get; init; }
        [JsonRequired] public double Scale { get; init; }
        [JsonRequired] public int Layer { get; init; }
    }

    private sealed class ImageDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public string? RelativePath { get; init; }

        [JsonRequired]
        public int PixelWidth { get; init; }

        [JsonRequired]
        public int PixelHeight { get; init; }
    }

    private sealed class CharacterDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public List<CharacterStateDocument?>? States { get; init; }

        [JsonRequired]
        public Guid? DefaultStateId { get; init; }
    }

    private sealed class CharacterStateDocument
    {
        [JsonRequired]
        public Guid Id { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public Guid ImageId { get; init; }
    }

    private sealed class DialogueUiDocument
    {
        [JsonRequired]
        public string? Css { get; init; }

        [JsonRequired]
        public bool ShowVoiceProgress { get; init; }

        [JsonRequired]
        public bool ShowVoiceControls { get; init; }

        [JsonRequired]
        public bool ShowNavigationControls { get; init; }
    }
}
