using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable bounded library example using ordinary component state and external Lua.</summary>
public static class ExampleLuaLibraryModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var added = ExampleComponentStateModule.AddTo(project);
        var script = added.Scripts.Last() with { Name = "example-lua-library.lua", Source = Source };
        var scene = added.Scenes.Last() with { Name = "Пример · библиотека Lua" };
        scene = scene with { Canvas = scene.Canvas with { Nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Библиотека Lua" },
            "description" => n with { Text = "Счёт до 10 · история трёх шагов · сброс и повторное открытие" },
            _ => n,
        }).ToImmutableArray() } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene),
            Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F3").Append(new("F3", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string Source = """
        -- Только разрешённые функции; каждый вызов ограничен бюджетом библиотеки.
        -- Для строк используйте string.sub(text, ...), без методов и Lua-шаблонов.
        local function initial() return {count=0,steps=ui.list({})} end
        local function show(s)
          if s.count==0 then ui.set_text(refs.state,'Готово к нажатию'); return end
          local captions={}
          for i,step in ipairs(s.steps) do table.insert(captions,tostring(step)) end
          ui.set_text(refs.state,table.concat({'Шагов: ',tostring(s.count),' · Последние: ',table.concat(captions,', ')}))
        end
        return {
          on_load=function()
            local s=ui.get_state()
            if s==ui.null then s=initial(); ui.set_state(s) end
            ui.set_text(refs.description,string.upper('lua')..' · math, string, table · история трёх шагов')
            show(s)
          end,
          on_event=function(e)
            if e.kind=='click' then
              if e.node==refs.increment then
                local s=ui.get_state()
                s.count=math.min(10,s.count+1)
                if #s.steps==3 then table.remove(s.steps,1) end
                table.insert(s.steps,s.count)
                ui.set_state(s); show(s)
              elseif e.node==refs.reset then
                local s=initial(); ui.set_state(s); show(s)
              end
            end
          end
        }
        """;
}
