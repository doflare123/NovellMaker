using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable example content, built on public APIs. The runtime contains no menu resources.</summary>
public static class ExampleGameMenusModule
{
    public static NovelProject AddTo(NovelProject project, Guid storyId)
    {
        if (!project.Scenes.Any(s => s.Id == storyId)) throw new ArgumentException("Выберите сюжетную сцену.");
        var menu = NovelScene.Create("Пример · главное меню");
        var slots = NovelScene.Create("Пример · сохранения");
        var root = slots.Canvas.Nodes[0].Id;
        var status = Guid.NewGuid(); var rows = Guid.NewGuid(); var template = Guid.NewGuid();
        var title = Guid.NewGuid(); var phrase = Guid.NewGuid(); var load = Guid.NewGuid(); var delete = Guid.NewGuid();
        var previous = Guid.NewGuid(); var next = Guid.NewGuid(); var save = Guid.NewGuid(); var refresh = Guid.NewGuid(); var close = Guid.NewGuid();
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-slots.lua", Source);
        slots = slots with { Canvas = slots.Canvas with
        {
            HandlerScriptId = script.Id,
            Nodes = slots.Canvas.Nodes.AddRange(new ScreenNode[] {
                new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(60, 25, 1100, 44), "Сохранения"),
                new(status, root, ScreenNodeKind.Text, new(60, 75, 1100, 44), "Чтение списка…"),
                new(rows, root, ScreenNodeKind.Container, new(60, 130, 1160, 390)),
                new(template, rows, ScreenNodeKind.Panel, new(0, 0, 1160, 60), Visible: false) { Name = "Шаблон строки слота" },
                new(title, template, ScreenNodeKind.Text, new(10, 2, 730, 26), "Название и дата"),
                new(phrase, template, ScreenNodeKind.Text, new(10, 30, 730, 28), "Фраза"),
                new(load, template, ScreenNodeKind.Button, new(760, 8, 170, 44), "Загрузить"),
                new(delete, template, ScreenNodeKind.Button, new(950, 8, 190, 44), "Удалить"),
                new(previous, root, ScreenNodeKind.Button, new(60, 545, 180, 48), "Ранее"),
                new(next, root, ScreenNodeKind.Button, new(255, 545, 180, 48), "Далее"),
                new(save, root, ScreenNodeKind.Button, new(450, 545, 240, 48), "Новое сохранение"),
                new(refresh, root, ScreenNodeKind.Button, new(705, 545, 220, 48), "Обновить список"),
                new(close, root, ScreenNodeKind.Button, new(940, 545, 280, 48), "Закрыть") }),
            References = ImmutableDictionary<string, Guid>.Empty.Add("status", status).Add("rows", rows).Add("template", template)
                .Add("title", title).Add("phrase", phrase).Add("load", load).Add("delete", delete).Add("previous", previous).Add("next", next).Add("save", save).Add("refresh", refresh),
            Buttons = [new(close, ScreenButtonAction.Close, null)]
        } };
        var menuRoot = menu.Canvas.Nodes[0].Id;
        var play = Guid.NewGuid(); var resume = Guid.NewGuid(); var browse = Guid.NewGuid(); var end = Guid.NewGuid();
        menu = menu with { Canvas = menu.Canvas with
        {
            Nodes = menu.Canvas.Nodes.AddRange(new ScreenNode[] {
                new(Guid.NewGuid(), menuRoot, ScreenNodeKind.Text, new(300, 90, 680, 65), project.Name),
                new(play, menuRoot, ScreenNodeKind.Button, new(390, 200, 500, 60), "Новая игра"),
                new(resume, menuRoot, ScreenNodeKind.Button, new(390, 280, 500, 60), "Продолжить игру"),
                new(browse, menuRoot, ScreenNodeKind.Button, new(390, 360, 500, 60), "Сохранения"),
                new(end, menuRoot, ScreenNodeKind.Button, new(390, 440, 500, 60), "Выйти") }),
            Buttons = [new(play, ScreenButtonAction.NewGame, storyId), new(resume, ScreenButtonAction.ContinueGame, null),
                new(browse, ScreenButtonAction.Open, slots.Id), new(end, ScreenButtonAction.EndSession, null)]
        } };
        var result = project with { Scenes = project.Scenes.Add(menu).Add(slots), Scripts = project.Scripts.Add(script), StartScreenSceneId = menu.Id };
        result = result with { InputBindings = result.InputBindings.Where(b => b.Key is not ("F2" or "F10"))
            .Concat(new ProjectInputBinding[] { new("F2", ScreenButtonAction.MainMenu, null), new("F10", ScreenButtonAction.Toggle, slots.Id) }).ToImmutableArray() };
        ScreenAsset.ValidateProject(result); return result;
    }

    public const string Source = """
        -- Данные приходят асинхронно; у Lua нет доступа к диску.
        local items = {}
        local page = 0
        local live = {}
        local node_maps = {}
        local loads = {}
        local deletes = {}
        local function render()
          loads = {}; deletes = {}
          if page*6 >= #items then page=0 end
          for row=1,6 do
            local slot = items[page*6+row]
            if slot then
              if not live[row] then live[row],node_maps[row] = ui.create(refs.template, refs.rows) end
              local panel = live[row]
              local nodes = node_maps[row]
              ui.set_visible(panel, true)
              ui.set_geometry(panel, {y=(row-1)*64})
              ui.set_text(nodes[refs.title], (slot.title or slot.id)..' · '..(slot.saved_at or 'Дата неизвестна'))
              ui.set_text(nodes[refs.phrase], slot.compatible and (slot.phrase or '') or (slot.diagnostic or slot.status))
              ui.set_enabled(nodes[refs.load], slot.compatible)
              loads[nodes[refs.load]]=slot.id
              deletes[nodes[refs.delete]]=slot.id
            elseif live[row] then ui.set_visible(live[row],false)
            end
          end
          ui.set_enabled(refs.previous, page>0)
          ui.set_enabled(refs.next, (page+1)*6<#items)
          ui.set_text(refs.status, 'Сохранений: '..#items..' · страница '..(page+1))
        end
        return {
          on_load = function() ui.list_slots() end,
          on_event = function(event)
            if event.kind == 'service_result' then
              if event.operation == 'ListSlots' and event.success then items=event.slots; render()
              else
                ui.set_text(refs.status,event.message)
                if event.success and (event.operation=='SaveSlot' or event.operation=='DeleteSlot') then ui.list_slots() end
              end
            elseif event.kind == 'click' then
              if loads[event.node] then ui.load_slot(loads[event.node])
              elseif deletes[event.node] then ui.delete_slot(deletes[event.node])
              elseif event.node==refs.save then ui.save_slot()
              elseif event.node==refs.refresh then ui.list_slots()
              elseif event.node==refs.previous and page>0 then page=page-1; render()
              elseif event.node==refs.next and (page+1)*6<#items then page=page+1; render() end
            end
          end
        }
        """;
}
