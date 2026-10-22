using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Screens;

public enum ScreenButtonAction { Lua, Close, Open, Replace, Back, None, Scene, Volume, Mute, Continue, Save, Load, Toggle, NewGame, MainMenu, EndSession }

public sealed record ScreenButtonBinding(Guid NodeId, ScreenButtonAction Action, Guid? TargetScreenId)
{
    public NovellMaker.Core.Audio.AudioGroup Group { get; init; }
    public double Amount { get; init; }
}

/// <summary>Authoring data; live instances and Lua closures never belong to a project snapshot.</summary>
public sealed record ScreenAsset(Guid Id, string Name, ImmutableArray<ScreenNode> Nodes)
{
    [JsonRequired] public Guid? HandlerScriptId { get; init; }
    [JsonRequired] public ImmutableDictionary<string, Guid> References { get; init; } = ImmutableDictionary<string, Guid>.Empty;
    [JsonRequired] public ImmutableArray<ScreenButtonBinding> Buttons { get; init; } = [];
    public bool PauseOnOverlay { get; init; } = true;
    public bool BlockInputOnOverlay { get; init; } = true;

    public ScreenDefinition Definition() => new(Id, Nodes);
    public NovellMaker.Core.Scenes.NovelScene ToScene() => new NovellMaker.Core.Scenes.NovelScene(Guid.NewGuid(), "Сцена")
        with { Id = Id, Name = Name, Canvas = NovellMaker.Core.Scenes.SceneCanvas.FromScreen(this) };

    public bool ContentEquals(ScreenAsset other) => Id == other.Id && Name == other.Name && HandlerScriptId == other.HandlerScriptId &&
        PauseOnOverlay == other.PauseOnOverlay && BlockInputOnOverlay == other.BlockInputOnOverlay &&
        Nodes.SequenceEqual(other.Nodes) && Buttons.SequenceEqual(other.Buttons) && References.Count == other.References.Count &&
        References.All(r => other.References.TryGetValue(r.Key, out var id) && id == r.Value);

    public static ScreenAsset Create(string name)
    {
        var id = Guid.NewGuid();
        return NovellMaker.Core.Scenes.SceneCanvas.Empty(id).AsScreen(id, name);
    }

    public static void ValidateProject(NovelProject project)
    {
        if (project.Scenes.IsDefault || project.Scenes.Any(s => s is null || s.Canvas is null))
            throw new ArgumentException("Отсутствует коллекция сцен или компонентов.");
        if (project.StartScreenSceneId is { } start && !project.Scenes.Any(s => s.Id == start))
            throw new ArgumentException("Стартовый экран отсутствует в проекте.");
        var ids = new HashSet<Guid> { project.Id };
        ids.UnionWith(project.Images.Select(i => i.Id));
        ids.UnionWith(project.Audio.Assets.Select(a => a.Id));
        ids.UnionWith(project.Scripts.Select(s => s.Id));
        ids.UnionWith(project.Characters.Select(c => c.Id));
        ids.UnionWith(project.Characters.SelectMany(c => c.States).Select(s => s.Id));
        foreach (var scene in project.Scenes)
            if (!ids.Add(scene.Id)) throw new ArgumentException("Повторный ID сцены.");
        ids.UnionWith(project.Scenes.SelectMany(s => s.Lines).Select(l => l.Id));
        ids.UnionWith(project.Scenes.SelectMany(s => s.Actions.Concat(s.ScriptBlocks.Select(b => b.Action))).Select(a => a.Id));
        foreach (var screen in project.Screens)
        {
            if (screen is null || string.IsNullOrWhiteSpace(screen.Name) || screen.Id == Guid.Empty)
                throw new ArgumentException("Некорректный экран: имя или повторный ID.");
            _ = screen.Definition();
            if (screen.Nodes.Any(n => n.ImageId is { } image && !project.Images.Any(i => i.Id == image)))
                throw new ArgumentException($"Экран «{screen.Name}»: изображение отсутствует.");
            if (screen.Nodes.Any(n => !ids.Add(n.Id))) throw new ArgumentException("Повторный ID узла экрана.");
            if (screen.HandlerScriptId is { } handler && !project.Scripts.Any(s => s.Id == handler))
                throw new ArgumentException($"Экран «{screen.Name}»: обработчик Lua отсутствует.");
            if (screen.References is null || !LuaActionScript.ValidReferences(screen.References) ||
                screen.References.Any(r => !screen.Nodes.Any(n => n.Id == r.Value)))
                throw new ArgumentException($"Экран «{screen.Name}»: refs должны ссылаться на собственные узлы.");
            if (screen.Buttons.IsDefault || screen.Buttons.Any(b => b is null || !Enum.IsDefined(b.Action) ||
                !screen.Nodes.Any(n => n.Id == b.NodeId && n.Kind is ScreenNodeKind.Button or ScreenNodeKind.Area) ||
                !Enum.IsDefined(b.Group) || !double.IsFinite(b.Amount) || Math.Abs(b.Amount) > 1 || b.Action != ScreenButtonAction.Volume && b.Amount != 0 ||
                (b.Action is ScreenButtonAction.Open or ScreenButtonAction.Replace or ScreenButtonAction.Scene or ScreenButtonAction.Toggle or ScreenButtonAction.NewGame
                    ? !project.Screens.Any(s => s is not null && s.Id == b.TargetScreenId)
                    : b.TargetScreenId is not null)) || screen.Buttons.Select(b => b.NodeId).Distinct().Count() != screen.Buttons.Length)
                throw new ArgumentException($"Экран «{screen.Name}»: неверная привязка кнопки.");
        }
        ProjectInput.Validate(project);
    }
}
