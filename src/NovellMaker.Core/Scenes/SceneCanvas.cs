using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Scenes;

/// <summary>Visual components and event bindings owned by a scene, independent of how it is opened.</summary>
public sealed record SceneCanvas
{
    [JsonRequired] public ImmutableArray<ScreenNode> Nodes { get; init; } = [];
    [JsonRequired] public Guid? HandlerScriptId { get; init; }
    [JsonRequired] public ImmutableDictionary<string, Guid> References { get; init; } = ImmutableDictionary<string, Guid>.Empty;
    [JsonRequired] public ImmutableArray<ScreenButtonBinding> Buttons { get; init; } = [];
    [JsonRequired] public bool PauseOnOverlay { get; init; } = true;
    [JsonRequired] public bool BlockInputOnOverlay { get; init; } = true;

    // Deterministic legacy migration: reopening an old project produces the same revision and node IDs.
    public static SceneCanvas Empty(Guid sceneId) => new()
    {
        Nodes = [new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"NovellMaker/canvas/{sceneId:D}"))[..16]),
            null, ScreenNodeKind.Container, new(0, 0, 1280, 720))]
    };

    public ScreenAsset AsScreen(Guid id, string name) => new(id, name, Nodes)
    {
        HandlerScriptId = HandlerScriptId, References = References, Buttons = Buttons,
        PauseOnOverlay = PauseOnOverlay, BlockInputOnOverlay = BlockInputOnOverlay
    };

    public static SceneCanvas FromScreen(ScreenAsset screen) => new()
    {
        Nodes = screen.Nodes, HandlerScriptId = screen.HandlerScriptId, References = screen.References,
        Buttons = screen.Buttons, PauseOnOverlay = screen.PauseOnOverlay, BlockInputOnOverlay = screen.BlockInputOnOverlay
    };
}
