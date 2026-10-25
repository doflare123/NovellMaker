using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Ordinary author data demonstrating logs and a rejected failing batch.</summary>
public static class ExampleLuaDiagnosticsModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var added = ExampleComponentStateModule.AddTo(project);
        var script = added.Scripts.Last() with { Name = "example-lua-diagnostics.lua", Source = Source };
        var scene = added.Scenes.Last() with { Name = "Пример · журнал Lua" };
        scene = scene with { Canvas = scene.Canvas with { Nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Журнал Lua" },
            "description" => n with { Text = "Журнал Lua F6 открывается в свойствах Lua-ресурса" },
            "state" => n with { Text = "Сообщение записано при открытии" },
            "increment" => n with { Text = "Записать сообщение" },
            "reset" => n with { Text = "Проверить ошибку" },
            _ => n,
        }).ToImmutableArray() } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene), Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F1").Append(new("F1", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string Source = """
        -- F6 фиксирует код: после изменения файла запустите F6 повторно.
        ---@type NMHandlers
        local handlers = {
          on_load=function()
            ui.log('info','Открыт пример журнала')
          end,
          ---@param e NMEvent
          on_event=function(e)
            if e.kind~='click' then return end
            if e.node==refs.increment then
              ui.log('info','Нажата кнопка записи')
              ui.set_text(refs.state,'Сообщение записано из click')
            elseif e.node==refs.reset then
              ui.log('warning','Сейчас будет намеренная ошибка')
              ui.set_text(refs.state,'Этот текст не должен примениться')
              local missing=nil
              missing()
            end
          end
        }
        return handlers
        """;
}
