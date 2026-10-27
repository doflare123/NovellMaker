using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Author-owned tween demonstration, editable like any other scene and Lua resource.</summary>
public static class ExampleAnimationsModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var added = ExampleComponentStateModule.AddTo(project);
        var script = added.Scripts.Last() with { Name = "example-animations.lua", Source = Source };
        var scene = added.Scenes.Last() with { Name = "Пример · анимации" };
        var nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Анимации компонентов" },
            "description" => n with { Text = "Движение и вращение параллельно, возврат последовательно · отмена сохраняет положение" },
            "state" => n with { Text = "Готово" },
            "increment" => n with { Text = "Запустить / заменить" },
            "reset" => n with { Text = "Отменить" },
            _ => n
        }).ToImmutableArray();
        var marker = Guid.NewGuid();
        nodes = nodes.Add(new(marker, nodes[0].Id, ScreenNodeKind.Text, new(200, 380, 80, 50), "Tween")
            { Name = "marker", Color = "#74B8EA", ClipChildren = false });
        scene = scene with { Canvas = scene.Canvas with { Nodes = nodes, References = scene.Canvas.References.Add("marker", marker) } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene), Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F11").Append(new("F11", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string Source = """
        -- Описания действуют в одном обработчике; ID запуска сохраняется до его завершения.
        local running=nil
        ---@type NMHandlers
        local handlers={}
        handlers.on_load=function() ui.set_text(refs.state,'Готово') end
        ---@param e NMEvent
        handlers.on_event=function(e)
          if e.kind=='click' and e.node==refs.increment then
            local outward=ui.parallel({
              ui.tween(refs.marker,{x=900},1,'in_out_quad'),
              ui.tween(refs.marker,{rotation=180,opacity=0.35},1,'out_cubic')
            })
            local home=ui.tween(refs.marker,{x=200,rotation=0,opacity=1},1,'in_out_cubic')
            running=ui.animate(ui.sequence({outward,home}))
            ui.set_text(refs.state,'Выполняется')
          elseif e.kind=='click' and e.node==refs.reset and running~=nil then
            ui.cancel_animation(running)
          elseif e.kind=='animation_result' and e.animation_id==running then
            ui.set_text(refs.state,e.status=='completed' and 'Завершено' or 'Отменено')
            running=nil
          end
        end
        return handlers
        """;
}
