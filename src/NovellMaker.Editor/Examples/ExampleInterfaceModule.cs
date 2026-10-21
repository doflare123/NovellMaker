using System.Collections.Immutable;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Example project content built exclusively on the public scene/component API.</summary>
public static class ExampleInterfaceModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var sound = NovelScene.Create("Пример · настройки звука");
        var root = Guid.NewGuid();
        var nodes = ImmutableArray.CreateBuilder<ScreenNode>();
        var buttons = ImmutableArray.CreateBuilder<ScreenButtonBinding>();
        nodes.Add(new(root, null, ScreenNodeKind.Panel, new(140, 110, 1000, 430)));
        foreach (var group in Enum.GetValues<AudioGroup>())
        {
            var y = 40 + (int)group * 70;
            nodes.Add(new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(40, y, 500, 52), group.ToString()));
            void Button(int x, string text, ScreenButtonAction action, double amount = 0)
            {
                var id = Guid.NewGuid(); nodes.Add(new(id, root, ScreenNodeKind.Button, new(x, y, 100, 52), text));
                buttons.Add(new(id, action, null) { Group = group, Amount = amount });
            }
            Button(570, "−10%", ScreenButtonAction.Volume, -.1);
            Button(680, "+10%", ScreenButtonAction.Volume, .1);
            Button(790, "Mute", ScreenButtonAction.Mute);
        }
        var back = Guid.NewGuid(); nodes.Add(new(back, root, ScreenNodeKind.Button, new(40, 340, 300, 52), "Назад"));
        buttons.Add(new(back, ScreenButtonAction.Close, null));
        sound = sound with { Canvas = sound.Canvas with { Nodes = nodes.ToImmutable(), Buttons = buttons.ToImmutable() } };
        var menu = NovelScene.Create("Пример · меню");
        var panel = Guid.NewGuid(); var title = Guid.NewGuid(); var resume = Guid.NewGuid(); var settings = Guid.NewGuid();
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-menu.lua", """
            return {
              on_load = function() ui.set_text(refs.title, "Меню проекта-примера") end,
              on_event = function(event)
                if event.node == refs.resume then ui.close() end
              end
            }
            """);
        menu = menu with { Canvas = menu.Canvas with
        {
            Nodes = [new(panel, null, ScreenNodeKind.Panel, new(360, 200, 560, 300)),
                new(title, panel, ScreenNodeKind.Text, new(40, 24, 480, 50), "Меню"),
                new(resume, panel, ScreenNodeKind.Button, new(40, 100, 480, 52), "Продолжить"),
                new(settings, panel, ScreenNodeKind.Button, new(40, 170, 480, 52), "Звук")],
            HandlerScriptId = script.Id,
            References = ImmutableDictionary<string, Guid>.Empty.Add("title", title).Add("resume", resume),
            Buttons = [new(resume, ScreenButtonAction.Lua, null), new(settings, ScreenButtonAction.Open, sound.Id)]
        } };
        return project with
        {
            Scenes = project.Scenes.Add(menu).Add(sound), Scripts = project.Scripts.Add(script),
            InputBindings = project.InputBindings.AddRange(new ProjectInputBinding[] {
                new("P", ScreenButtonAction.Toggle, menu.Id), new("F9", ScreenButtonAction.Toggle, sound.Id),
                new("F7", ScreenButtonAction.Save, null), new("F8", ScreenButtonAction.Load, null)})
        };
    }
}
