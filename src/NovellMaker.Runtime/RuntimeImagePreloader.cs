using NovellMaker.Core.Assets;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Runtime;

/// <summary>Bounded declarative hints. Never executes scenario actions or Lua to predict a transition.</summary>
internal sealed class RuntimeImagePreloader
{
    private readonly Dictionary<Guid, NovelScene> scenes;
    private readonly Dictionary<Guid, ImageAsset> images;
    private readonly Dictionary<Guid, Guid> states;
    private readonly Dictionary<Guid, Guid> defaultImages;
    private readonly Dictionary<Guid, Guid[]> edges;
    private readonly Guid[] hotkeyScenes;

    internal RuntimeImagePreloader(NovelProject project)
    {
        scenes = project.Scenes.ToDictionary(s => s.Id);
        images = project.Images.ToDictionary(i => i.Id);
        states = project.Characters.SelectMany(c => c.States).ToDictionary(s => s.Id, s => s.ImageId);
        defaultImages = project.Characters.Where(c => !c.States.IsEmpty).ToDictionary(c => c.Id,
            c => (c.States.FirstOrDefault(s => s.Id == c.DefaultStateId) ?? c.States[0]).ImageId);
        edges = project.Scenes.ToDictionary(s => s.Id, s => Targets(s.Actions.Concat(s.ScriptBlocks.Select(b => b.Action)))
            .Concat(s.Canvas.Buttons.Select(b => b.TargetScreenId).OfType<Guid>()).Distinct().Take(4).ToArray());
        hotkeyScenes = project.InputBindings.Select(b => b.TargetSceneId).OfType<Guid>().Distinct().Take(4).ToArray();
    }

    private static IEnumerable<Guid> Targets(IEnumerable<ScenarioAction> actions)
    {
        foreach (var action in actions)
        {
            if (action is JumpAction jump) yield return jump.SceneId;
            else if (action is BranchAction branch) yield return branch.SceneId;
            else if (action is ChoiceAction choice)
                foreach (var option in choice.Options) yield return option.TargetSceneId;
        }
    }

    internal ImageAsset[] Hints(GameState state, SceneFrame frame, IEnumerable<Guid> visibleSceneIds)
    {
        IEnumerable<Guid> LineImages(NovelScene scene, int first)
        {
            for (var i = Math.Max(0, first); i < Math.Min(scene.Lines.Length, first + 4); i++)
            {
                var line = scene.Lines[i];
                if (line.CharacterStateId is { } pose && states.TryGetValue(pose, out var image)) yield return image;
                else if (line.SpeakerId is { } speaker)
                {
                    if (state.ObjectStates.TryGetValue(speaker, out var current) && states.TryGetValue(current, out image)) yield return image;
                    else if (defaultImages.TryGetValue(speaker, out image)) yield return image;
                }
            }
        }
        IEnumerable<Guid> Candidates()
        {
            if (scenes.TryGetValue(state.SceneId, out var current))
                foreach (var id in LineImages(current, frame.LineIndex + 1)) yield return id;
            var targets = (state.Choice?.Options.Select(o => o.TargetSceneId) ?? [])
                .Concat(visibleSceneIds.Prepend(state.SceneId).Distinct().SelectMany(id => edges.GetValueOrDefault(id) ?? []))
                .Concat(hotkeyScenes).Where(id => id != state.SceneId).Distinct().Take(4);
            foreach (var target in targets)
            {
                if (!scenes.TryGetValue(target, out var scene)) continue;
                if (scene.BackgroundImageId is { } background) yield return background;
                foreach (var node in scene.Canvas.Nodes.Where(n => n.Visible && n.ImageId is not null).Take(8)) yield return node.ImageId!.Value;
                foreach (var id in LineImages(scene, 0)) yield return id;
            }
        }
        return Candidates().Distinct().Select(id => images.GetValueOrDefault(id)).OfType<ImageAsset>().Take(16).ToArray();
    }
}
