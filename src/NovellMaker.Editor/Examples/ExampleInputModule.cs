using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable author-owned input demonstration. No runtime-specific scene behavior.</summary>
public static class ExampleInputModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var added = ExampleComponentStateModule.AddTo(project);
        var script = added.Scripts.Last() with { Name = "example-input.lua", Source = Source };
        var scene = added.Scenes.Last() with { Name = "Пример · расширенный ввод" };
        var nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Указатель и клавиатура" },
            "description" => n with { Text = "Наведите и перетащите область. Tab — фокус, стрелки — сдвиг. Esc — закрыть." },
            "state" => n with { Text = "Готово" },
            "increment" => n with { Text = "Перетащите меня", Name = "drag", Bounds = new(200, 380, 220, 64) },
            "reset" => n with { Text = "Вернуть в начало" },
            _ => n
        }).ToImmutableArray();
        scene = scene with { Canvas = scene.Canvas with { Nodes = nodes,
            References = scene.Canvas.References.Remove("increment").Add("drag", nodes.Single(n => n.Name == "drag").Id) } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene),
            Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F12").Append(new("F12", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string Source = """
        local x,y=200,380
        local dx,dy=0,0
        local held=false
        local function place()
          ui.set_geometry(refs.drag,{x=x,y=y})
        end
        ---@type NMHandlers
        local handlers={input_events=true}
        ---@param e NMEvent
        handlers.on_event=function(e)
          if e.kind=='pointer_down' and e.node==refs.drag then
            held=true;dx=e.x-x;dy=e.y-y
            ui.capture_pointer(refs.drag);ui.focus(refs.drag)
          elseif e.kind=='drag' and held then
            x=math.max(0,math.min(1060,e.x-dx));y=math.max(0,math.min(656,e.y-dy));place()
            ui.set_text(refs.state,'Перетаскивание')
          elseif e.kind=='pointer_up' or e.kind=='pointer_cancel' or e.kind=='input_reset' then
            held=false;ui.set_text(refs.state,'Захват снят')
          elseif e.kind=='hover_enter' then ui.set_text(refs.state,'Наведение')
          elseif e.kind=='hover_leave' then ui.set_text(refs.state,'Указатель вне области')
          elseif e.kind=='focus' then ui.set_text(refs.state,'Фокус')
          elseif e.kind=='key_down' and e.node==refs.drag then
            if e.key=='Left' then x=math.max(0,x-20)
            elseif e.key=='Right' then x=math.min(1060,x+20)
            elseif e.key=='Up' then y=math.max(0,y-20)
            elseif e.key=='Down' then y=math.min(656,y+20) end
            place();ui.set_text(refs.state,'Клавиша '..e.key)
          elseif e.kind=='click' and e.node==refs.reset then
            x=200;y=380;place();ui.focus(refs.drag)
          end
        end
        return handlers
        """;
}
