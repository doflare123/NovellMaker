using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Screens;

/// <summary>Materializes ordinary editable project resources. Never used to construct runtime windows.</summary>
public static class ReadingScreenTemplates
{
    public static bool IsWindow(ScreenButtonAction action) => action is ScreenButtonAction.History or ScreenButtonAction.ReadingSettings;

    public static NovelProject Ensure(NovelProject project)
    {
        var result = project;
        foreach (var action in new[] { ScreenButtonAction.History, ScreenButtonAction.ReadingSettings })
        {
            var target = project.Scenes.SelectMany(s => s.Canvas.Buttons).Where(b => b.Action == action).Select(b => b.TargetScreenId)
                .Concat(project.InputBindings.Where(b => b.Action == action).Select(b => b.TargetSceneId)).FirstOrDefault(id => id is not null);
            if (target is null)
            {
                var (scene, script) = Create(project.Id, action);
                // Also allow adding controls to a second scene after the first controls were removed.
                if (!result.Scenes.Any(s => s.Id == scene.Id)) result = result with { Scenes = result.Scenes.Add(scene),
                    Scripts = result.Scripts.Any(s => s.Id == script.Id) ? result.Scripts : result.Scripts.Add(script) };
                target = scene.Id;
            }
            result = result with
            {
                Scenes = [.. result.Scenes.Select(s => s with { Canvas = s.Canvas with
                { Buttons = [.. s.Canvas.Buttons.Select(b => b.Action == action && b.TargetScreenId is null ? b with { TargetScreenId = target } : b)] } })],
                InputBindings = [.. result.InputBindings.Select(b => b.Action == action && b.TargetSceneId is null ? b with { TargetSceneId = target } : b)]
            };
        }
        return result;
    }

    private static (NovelScene, LuaScriptAsset) Create(Guid project, ScreenButtonAction action)
    {
        Guid Id(string part) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"NovellMaker/reading/{project:D}/{action}/{part}"))[..16]);
        var history = action == ScreenButtonAction.History;
        var root = Id("root");
        var nodes = ImmutableArray.CreateBuilder<ScreenNode>();
        var refs = ImmutableDictionary.CreateBuilder<string, Guid>();
        nodes.Add(new(root, null, ScreenNodeKind.Panel, ScreenDefinition.CanvasViewport));
        void Node(string name, ScreenNodeKind kind, double x, double y, double width, double height, string text, bool visible = true)
        {
            var id = Id(name); refs.Add(name, id);
            nodes.Add(new(id, root, kind, new(x, y, width, height), text, visible) { Name = name });
        }
        void Text(string name, double y, string text, bool visible = true) => Node(name, ScreenNodeKind.Text, 80, y, 1120, 32, text, visible);
        void Button(string name, double x, double y, double width, string text) => Node(name, ScreenNodeKind.Button, x, y, width, 44, text);
        Text("title", 32, history ? "История реплик" : "Настройки чтения");
        Button("close", 1040, 642, 160, "Закрыть");
        Text("error", history ? 545 : 580, "Не удалось выполнить действие. Повторите попытку.", false);
        if (history)
        {
            Text("empty", 110, "В этом сохранении пока нет истории реплик.", false);
            Text("position", 76, "");
            Text("speaker", 110, "");
            Node("body", ScreenNodeKind.Text, 80, 150, 1120, 380, "");
            Button("previous", 80, 590, 160, "Ранее"); Button("next", 250, 590, 160, "Позже");
            Button("page_previous", 440, 590, 180, "Страница −"); Button("page_next", 630, 590, 180, "Страница +");
            Button("replay", 850, 590, 350, "Повторить голос");
        }
        else
        {
            Text("speed_label", 100, "Скорость текста (знаков/с; 0 — мгновенно)"); Text("speed", 140, "");
            Button("slower", 80, 180, 220, "Медленнее"); Button("faster", 320, 180, 220, "Быстрее"); Button("instant", 560, 180, 220, "Мгновенно");
            Text("delay_label", 255, "Авточтение: задержка после текста (с)"); Text("delay", 290, "");
            Button("delay_down", 80, 335, 220, "−0,5 с"); Button("delay_up", 320, 335, 220, "+0,5 с");
            Button("wait_voice", 80, 430, 700, "Переключить ожидание озвучки");
            Text("wait_on", 485, "Ждать окончания голоса: да"); Text("wait_off", 485, "Ждать окончания голоса: нет", false);
            Text("hint", 530, "Пропуск останавливается на непрочитанной реплике, выборе и мини-игре.");
        }
        var script = new LuaScriptAsset(Id("script"), $"reading-{Id("script"):N}.lua", history ? HistorySource : SettingsSource);
        var scene = new NovelScene(Id("scene"), history ? "История реплик" : "Настройки чтения")
        { Canvas = new SceneCanvas { Nodes = nodes.ToImmutable(), References = refs.ToImmutable(), HandlerScriptId = script.Id,
            Buttons = [new(Id("close"), ScreenButtonAction.Close, null)] } };
        return (scene, script);
    }

    public const string HistorySource = """
        -- Change page_size and the body bounds together when designing another layout.
        local page_size=500
        local request
        local row
        local function enabled(value)
          ui.set_enabled(refs.previous,value and row and row.index>1 or false)
          ui.set_enabled(refs.next,value and row and row.index<row.count or false)
          ui.set_enabled(refs.page_previous,value and row and row.page>1 or false)
          ui.set_enabled(refs.page_next,value and row and row.page<row.pages or false)
          ui.set_enabled(refs.replay,value and row and row.can_replay or false)
        end
        local function read(index,page)
          request=ui.get_history(index,page,page_size)
          enabled(false)
          ui.set_visible(refs.error,false)
        end
        return {
          on_load=function() read(0,1) end,
          on_event=function(e)
            if e.kind=='service_result' and e.request_id==request then
              request=nil
              ui.set_visible(refs.error,not e.success)
              if e.history then
                row=e.history
                ui.set_visible(refs.empty,row.count==0)
                ui.set_text(refs.position,row.count==0 and '' or row.index..' / '..row.count..' · '..row.page..' / '..row.pages..' · '..row.language)
                ui.set_text(refs.speaker,row.speaker)
                ui.set_text(refs.body,row.text)
              end
              enabled(true)
            elseif e.kind=='click' and not request and row then
              if e.node==refs.previous then read(row.index-1,1)
              elseif e.node==refs.next then read(row.index+1,1)
              elseif e.node==refs.page_previous then read(row.index,row.page-1)
              elseif e.node==refs.page_next then read(row.index,row.page+1)
              elseif e.node==refs.replay and row.visit_id then
                request=ui.replay_history_voice(row.visit_id); enabled(false)
              end
            end
          end
        }
        """;

    public const string SettingsSource = """
        local request
        local values
        local controls={'slower','faster','instant','delay_down','delay_up','wait_voice'}
        local function enabled(value)
          for i=1,#controls do ui.set_enabled(refs[controls[i]],value) end
        end
        local function set(key,value)
          request=ui.set_setting(key,value); enabled(false); ui.set_visible(refs.error,false)
        end
        return {
          on_load=function() request=ui.get_settings(); enabled(false) end,
          on_event=function(e)
            if e.kind=='service_result' and e.request_id==request then
              request=nil
              ui.set_visible(refs.error,not e.success or e.persisted==false)
              if e.settings then
                values=e.settings
                ui.set_text(refs.speed,values.text_speed..'')
                ui.set_text(refs.delay,values.auto_delay..'')
                ui.set_visible(refs.wait_on,values.auto_wait_voice)
                ui.set_visible(refs.wait_off,not values.auto_wait_voice)
              end
              enabled(values~=nil)
            elseif e.kind=='click' and not request and values then
              if e.node==refs.slower then set('text_speed',math.max(0,values.text_speed-10))
              elseif e.node==refs.faster then set('text_speed',math.min(200,values.text_speed+10))
              elseif e.node==refs.instant then set('text_speed',0)
              elseif e.node==refs.delay_down then set('auto_delay',math.max(0,values.auto_delay-0.5))
              elseif e.node==refs.delay_up then set('auto_delay',math.min(30,values.auto_delay+0.5))
              elseif e.node==refs.wait_voice then set('auto_wait_voice',not values.auto_wait_voice) end
            end
          end
        }
        """;
}
