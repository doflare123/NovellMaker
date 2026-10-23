using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable explicit data example; no example-specific behavior in Core or Runtime.</summary>
public static class ExampleComponentStateModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var scene = NovelScene.Create("Пример · состояние компонентов");
        var root = scene.Canvas.Nodes[0].Id;
        var nodes = scene.Canvas.Nodes.ToBuilder();
        var refs = ImmutableDictionary.CreateBuilder<string, Guid>();
        Guid Node(string name, ScreenNodeKind kind, ScreenRect bounds, string text)
        {
            var id = Guid.NewGuid(); refs.Add(name, id);
            nodes.Add(new(id, root, kind, bounds, text) { Name = name }); return id;
        }
        Node("title", ScreenNodeKind.Text, new(150, 65, 980, 60), "Состояние компонентов");
        Node("description", ScreenNodeKind.Text, new(150, 150, 980, 100), "Список и словарь Lua · данные живут до закрытия этого экземпляра");
        Node("state", ScreenNodeKind.Text, new(150, 290, 980, 65), "Готово к нажатию");
        Node("increment", ScreenNodeKind.Button, new(150, 460, 460, 65), "Добавить шаг");
        Node("reset", ScreenNodeKind.Button, new(650, 460, 480, 65), "Сбросить данные");
        var close = Node("close", ScreenNodeKind.Button, new(790, 585, 340, 56), "Закрыть");
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-component-state.lua", Source);
        scene = scene with { Canvas = scene.Canvas with { HandlerScriptId = script.Id, Nodes = nodes.ToImmutable(),
            References = refs.ToImmutable(), Buttons = [new(close, ScreenButtonAction.Close, null)] } };
        var result = project with { Scenes = project.Scenes.Add(scene), Scripts = project.Scripts.Add(script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F7").Append(new("F7", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
        ScreenAsset.ValidateProject(result); return result;
    }

    public const string Source = """
        -- Явные данные: изменения копии действуют только после ui.set_state.
        -- Они принадлежат этому экземпляру, не записываются в профиль/слот.
        -- Новый F6 загружает свежий код, без подмены уже работающей VM.
        local function show(s)
          ui.set_text(refs.state,s.count==0 and 'Готово к нажатию' or 'Шагов: '..s.count)
        end
        return {
          on_load=function()
            local s=ui.get_state()
            if s==ui.null then
              s={count=0,steps=ui.list({}),selected=ui.null,active=true}
              ui.set_state(s)
            end
            show(s)
          end,
          on_event=function(e)
            if e.kind=='click' then
              if e.node==refs.increment then
                local s=ui.get_state()
                if s.count<10 then
                  s.count=s.count+1
                  s.steps[s.count]={number=s.count,done=false}
                  ui.set_state(s)
                  show(s)
                end
              elseif e.node==refs.reset then
                local s={count=0,steps=ui.list({}),selected=ui.null,active=true}
                ui.set_state(s)
                show(s)
              end
            end
          end
        }
        """;
}
