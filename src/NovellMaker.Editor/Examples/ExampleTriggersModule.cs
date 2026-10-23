using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable profile example. Stable keys belong to the author's Lua, captions to the scene.</summary>
public static class ExampleTriggersModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var scene = NovelScene.Create("Пример · триггеры и данные");
        var root = scene.Canvas.Nodes[0].Id;
        var nodes = scene.Canvas.Nodes.ToBuilder();
        var refs = ImmutableDictionary.CreateBuilder<string, Guid>();
        Guid Node(string name, ScreenNodeKind kind, ScreenRect bounds, string text)
        {
            var id = Guid.NewGuid(); refs.Add(name, id);
            nodes.Add(new(id, root, kind, bounds, text) { Name = name }); return id;
        }
        Node("title", ScreenNodeKind.Text, new(150, 65, 980, 60), "Триггеры и постоянные данные");
        Node("description", ScreenNodeKind.Text, new(150, 150, 980, 85), "Первый шаг · авторская отметка, независимая от слотов");
        Node("state", ScreenNodeKind.Text, new(150, 265, 980, 60), "Чтение…");
        Node("status", ScreenNodeKind.Text, new(150, 345, 980, 85), "");
        Node("unlock", ScreenNodeKind.Button, new(150, 460, 460, 65), "Послать триггер");
        Node("refresh", ScreenNodeKind.Button, new(650, 460, 480, 65), "Прочитать данные");
        var close = Node("close", ScreenNodeKind.Button, new(790, 585, 340, 56), "Закрыть");
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-triggers.lua", Source);
        scene = scene with { Canvas = scene.Canvas with { HandlerScriptId = script.Id, Nodes = nodes.ToImmutable(),
            References = refs.ToImmutable(), Buttons = [new(close, ScreenButtonAction.Close, null)] } };
        var result = project with { Scenes = project.Scenes.Add(scene), Scripts = project.Scripts.Add(script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F8").Append(new("F8", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
        ScreenAsset.ValidateProject(result); return result;
    }

    // All meaning of this mark lives in the author's resource, not in Core or Runtime.
    public const string Source = """
        -- Автор сам выбирает ключ, условие, одноразовость и отображение.
        local key='first_steps'
        local request
        local value
        local function busy(state)
          ui.set_enabled(refs.unlock,not state)
          ui.set_enabled(refs.refresh,not state)
        end
        return {
          on_load=function() request=ui.get_persistent(key); busy(true) end,
          on_event=function(e)
            if e.kind=='service_result' and e.request_id==request then
              request=nil
              value=e.value
              busy(false)
              if value=='true' then
                ui.set_text(refs.state,e.persisted and 'Отмечено: Первый шаг' or 'Не записано: Первый шаг')
              else ui.set_text(refs.state,'Отметки ещё нет') end
              ui.set_text(refs.status,e.message)
            elseif e.kind=='trigger' and e.trigger_id=='first_steps' and not request then
              -- Идемпотентное подтверждение: сохранённое значение не пишет файл снова,
              -- а оставшееся в памяти после отказа повторяет запись даже после чтения.
              request=ui.set_persistent(key,'true')
              busy(true)
            elseif e.kind=='click' and not request then
              if e.node==refs.unlock then
                ui.trigger('first_steps','button')
              elseif e.node==refs.refresh then
                request=ui.get_persistent(key)
                busy(true)
              end
            end
          end
        }
        """;
}
