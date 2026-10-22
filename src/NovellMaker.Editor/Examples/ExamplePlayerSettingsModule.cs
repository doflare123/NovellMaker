using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Ordinary editable scene and external script, added only on the author's request.</summary>
public static class ExamplePlayerSettingsModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var scene = NovelScene.Create("Пример · настройки игрока");
        var root = scene.Canvas.Nodes[0].Id;
        var nodes = scene.Canvas.Nodes.ToBuilder();
        var refs = ImmutableDictionary.CreateBuilder<string, Guid>();
        Guid Node(string name, ScreenNodeKind kind, ScreenRect bounds, string text)
        {
            var id = Guid.NewGuid(); refs.Add(name, id);
            nodes.Add(new(id, root, kind, bounds, text) { Name = name }); return id;
        }
        Node("title", ScreenNodeKind.Text, new(150, 35, 980, 56), "Настройки");
        Node("status", ScreenNodeKind.Text, new(150, 96, 980, 65), "Чтение настроек…");
        string[] groups = ["master", "sound", "effect", "voice"];
        string[] labels = ["Общая громкость", "Музыка и окружение", "Эффекты", "Голоса"];
        for (var i = 0; i < groups.Length; i++)
        {
            var y = 175 + i * 76; var name = groups[i];
            Node(name + "_label", ScreenNodeKind.Text, new(150, y, 460, 52), labels[i]);
            Node(name + "_down", ScreenNodeKind.Button, new(640, y, 80, 52), "−");
            Node(name + "_up", ScreenNodeKind.Button, new(740, y, 80, 52), "+");
            Node(name + "_mute", ScreenNodeKind.Button, new(850, y, 280, 52), "Отключить");
        }
        Node("fullscreen", ScreenNodeKind.Button, new(150, 495, 640, 56), "Полноэкранный режим");
        Node("refresh", ScreenNodeKind.Button, new(150, 585, 340, 56), "Обновить");
        var close = Node("close", ScreenNodeKind.Button, new(790, 585, 340, 56), "Закрыть");
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-player-settings.lua", Source);
        scene = scene with { Canvas = scene.Canvas with { HandlerScriptId = script.Id, Nodes = nodes.ToImmutable(),
            References = refs.ToImmutable(), Buttons = [new(close, ScreenButtonAction.Close, null)] } };
        var result = project with { Scenes = project.Scenes.Add(scene), Scripts = project.Scripts.Add(script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F9").Append(new("F9", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
        ScreenAsset.ValidateProject(result); return result;
    }

    public const string Source = """
        -- Профиль хранится отдельно от слотов. Запрос возвращает request_id.
        local values
        local request
        local groups={'master','sound','effect','voice'}
        local labels={'Общая громкость','Музыка и окружение','Эффекты','Голоса'}
        local function enabled(value)
          for i=1,4 do
            local key=groups[i]
            ui.set_enabled(refs[key..'_down'],value)
            ui.set_enabled(refs[key..'_up'],value)
            ui.set_enabled(refs[key..'_mute'],value)
          end
          ui.set_enabled(refs.fullscreen,value)
          ui.set_enabled(refs.refresh,value)
        end
        local function render()
          if not values then return end
          for i=1,4 do
            local key=groups[i]
            local percent=values[key..'_volume']*100+0.5
            percent=percent-percent%1
            ui.set_text(refs[key..'_label'],labels[i]..': '..percent..'%')
            ui.set_text(refs[key..'_mute'],values[key..'_muted'] and 'Включить' or 'Отключить')
          end
          ui.set_text(refs.fullscreen,values.fullscreen and 'Режим окна: полноэкранный' or 'Режим окна: оконный')
        end
        local function set(key,value)
          request=ui.set_setting(key,value)
          enabled(false)
          ui.set_text(refs.status,'Сохранение настроек…')
        end
        return {
          on_load=function() request=ui.get_settings(); enabled(false) end,
          on_event=function(event)
            if event.kind=='service_result' and event.request_id==request then
              request=nil
              if event.settings then values=event.settings; render() end
              enabled(true)
              ui.set_text(refs.status,event.message)
            elseif event.kind=='click' and not request then
              if event.node==refs.refresh then request=ui.get_settings(); enabled(false)
              elseif values then
                if event.node==refs.fullscreen then set('fullscreen',not values.fullscreen)
                else
                  for i=1,4 do
                    local key=groups[i]
                    if event.node==refs[key..'_mute'] then set(key..'_muted',not values[key..'_muted'])
                    elseif event.node==refs[key..'_down'] or event.node==refs[key..'_up'] then
                      local delta=event.node==refs[key..'_down'] and -0.1 or 0.1
                      local volume=values[key..'_volume']+delta
                      if volume<0 then volume=0 elseif volume>1 then volume=1 end
                      set(key..'_volume',volume)
                    end
                  end
                end
              end
            end
          end
        }
        """;
}
