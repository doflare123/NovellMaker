using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable project buttons. No runtime shortcuts are installed implicitly.</summary>
public static class ReadingControlsModule
{
    public static NovelProject AddTo(NovelProject project, Guid sceneId)
    {
        var scene = project.Scenes.First(s => s.Id == sceneId);
        var nodes = scene.Canvas.Nodes.ToBuilder(); var buttons = scene.Canvas.Buttons.ToBuilder();
        if (!scene.Lines.IsEmpty && !nodes.Any(n => n.Dialogue == DialogueBinding.Text))
        {
            var style = NovellMaker.Core.Styling.DialogueCssParser.Parse(scene.DialogueCssOverride ?? project.DialogueUi.Css);
            nodes = DialogueCanvas.Add(nodes.ToImmutable(), sceneId, style).ToBuilder();
        }
        (string Label, ScreenButtonAction Action)[] controls = [
            ("История", ScreenButtonAction.History), ("Авто", ScreenButtonAction.AutoRead),
            ("Пропуск", ScreenButtonAction.SkipRead), ("Чтение", ScreenButtonAction.ReadingSettings)];
        for (var i = 0; i < controls.Length; i++)
        {
            var (label, action) = controls[i];
            if (buttons.Any(b => b.Action == action)) continue;
            var id = Guid.NewGuid();
            nodes.Add(new(id, nodes.Single(n => n.ParentId is null).Id, ScreenNodeKind.Button, new(16 + i * 152, 660, 144, 44), label)
            { Name = label, Order = 2000 });
            buttons.Add(new(id, action, null));
        }
        var result = project with { Scenes = project.Scenes.Replace(scene, scene with { Canvas = scene.Canvas with { Nodes = nodes.ToImmutable(), Buttons = buttons.ToImmutable() } }) };
        ScreenAsset.ValidateProject(result); return result;
    }
}
