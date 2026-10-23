using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable handler + counter + formatting resources. Dependencies are ordinary stable-ID bindings.</summary>
public static class ExampleLuaModulesModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var added = ExampleComponentStateModule.AddTo(project);
        var formatting = new LuaScriptAsset(Guid.NewGuid(), "example-module-caption.lua", """
            return {caption=function(n) return n==0 and 'Готово к нажатию' or 'Шагов: '..n end}
            """);
        var counter = new LuaScriptAsset(Guid.NewGuid(), "example-module-counter.lua", CounterSource)
        { References = ImmutableDictionary<string, Guid>.Empty.Add("formatting", formatting.Id) };
        var handler = added.Scripts.Last() with { Name = "example-module-handler.lua", Source = HandlerSource,
            References = ImmutableDictionary<string, Guid>.Empty.Add("counter", counter.Id) };
        var scene = added.Scenes.Last() with { Name = "Пример · модули Lua" };
        scene = scene with { Canvas = scene.Canvas with { Nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Модули Lua" },
            "description" => n with { Text = "Обработчик → счётчик → оформление · зарегистрированные Lua-ресурсы" },
            _ => n,
        }).ToImmutableArray() } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene),
            Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, handler).Add(counter).Add(formatting),
            InputBindings = project.InputBindings.Where(b => b.Key != "F4").Append(new("F4", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string CounterSource = """
        local formatting=ui.import(modules.formatting)
        return {
          caption=function(s) return formatting.caption(s.count) end,
          advance=function(s)
            if s.count<10 then
              s.count=s.count+1
              s.steps[s.count]={number=s.count,done=false}
            end
            return s
          end
        }
        """;
    public const string HandlerSource = """
        -- Связи модулей настраиваются у Lua-ресурсов, refs узлов — у холста.
        -- Каждый модуль загружается один раз на экземпляр. Новый F6 берёт новый код.
        local counter=ui.import(modules.counter)
        local function initial() return {count=0,steps=ui.list({}),selected=ui.null} end
        local function show(s) ui.set_text(refs.state,counter.caption(s)) end
        return {
          on_load=function()
            local s=ui.get_state()
            if s==ui.null then s=initial(); ui.set_state(s) end
            show(s)
          end,
          on_event=function(e)
            if e.kind=='click' then
              if e.node==refs.increment then
                local s=counter.advance(ui.get_state())
                ui.set_state(s); show(s)
              elseif e.node==refs.reset then
                local s=initial(); ui.set_state(s); show(s)
              end
            end
          end
        }
        """;
}
