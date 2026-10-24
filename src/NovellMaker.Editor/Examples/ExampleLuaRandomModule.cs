using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable explicit RNG checkpoint/replay; all behavior belongs to the author script.</summary>
public static class ExampleLuaRandomModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var added = ExampleComponentStateModule.AddTo(project);
        var script = added.Scripts.Last() with { Name = "example-lua-random.lua", Source = Source };
        var scene = added.Scenes.Last() with { Name = "Пример · управляемый RNG" };
        scene = scene with { Canvas = scene.Canvas with { Nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Управляемый RNG" },
            "description" => n with { Text = "Seed 42 · числа 0–9999 · повтор из явного снимка" },
            "state" => n with { Text = "Нажмите «Следующее число»" },
            "increment" => n with { Text = "Следующее число" },
            "reset" => n with { Text = "Повторить из снимка" },
            _ => n,
        }).ToImmutableArray() } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene), Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F2").Append(new("F2", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string Source = """
        -- RNG принадлежит экземпляру. Часы ОС не используются.
        -- Снимок — строка с версией алгоритма; его можно хранить в явных данных.
        local function show(s)
          ui.set_text(refs.state,s.last==ui.null and 'Нажмите «Следующее число»' or 'Число: '..tostring(s.last))
        end
        return {
          on_load=function()
            local s=ui.get_state()
            if s==ui.null then
              ui.seed_random(42)
              s={last=ui.null,before=ui.get_random_state(),rng=ui.get_random_state()}
              ui.set_state(s)
            else
              ui.set_random_state(s.rng)
            end
            show(s)
          end,
          on_event=function(e)
            if e.kind=='click' then
              local s=ui.get_state()
              if e.node==refs.increment then
                s.before=ui.get_random_state()
                s.last=ui.random(0,9999)
              elseif e.node==refs.reset and s.last~=ui.null then
                ui.set_random_state(s.before)
                s.last=ui.random(0,9999)
              else return end
              s.rng=ui.get_random_state()
              ui.set_state(s); show(s)
            end
          end
        }
        """;
}
