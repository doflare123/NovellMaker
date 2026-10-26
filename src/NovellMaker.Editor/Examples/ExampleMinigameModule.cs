using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Editor.Examples;

/// <summary>Author-owned card puzzle. The engine knows only lifecycle/data, never cards or scoring.</summary>
public static class ExampleMinigameModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var puzzle = NovelScene.Create("Пример · карточный пазл");
        var root = puzzle.Canvas.Nodes[0].Id;
        var nodes = puzzle.Canvas.Nodes.ToBuilder();
        var refs = ImmutableDictionary.CreateBuilder<string, Guid>();
        void Node(string name, ScreenNodeKind kind, ScreenRect bounds, string text)
        { var id = Guid.NewGuid(); refs.Add(name, id); nodes.Add(new(id, root, kind, bounds, text) { Name = name }); }
        Node("title", ScreenNodeKind.Text, new(140, 40, 1000, 60), "Найдите четыре пары");
        Node("score", ScreenNodeKind.Text, new(140, 115, 1000, 60), "Ходов: 0 · Пар: 0/4");
        for (var i = 0; i < 8; i++) Node("card" + (i + 1), ScreenNodeKind.Button, new(140 + i % 4 * 250, 220 + i / 4 * 130, 220, 100), "?");
        Node("save", ScreenNodeKind.Button, new(140, 525, 290, 60), "Сохранить пазл");
        Node("load", ScreenNodeKind.Button, new(475, 525, 290, 60), "Загрузить пазл");
        Node("cancel", ScreenNodeKind.Button, new(810, 525, 290, 60), "Отмена");
        Node("status", ScreenNodeKind.Text, new(140, 610, 1000, 60), "Раскладка и генератор сохраняются вместе с сюжетом.");
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-card-puzzle.lua", Source);
        MinigameSchema Schema(params (string Name, LuaValueKind Kind)[] fields) => new() { Fields = fields.ToImmutableDictionary(p => p.Name, p => p.Kind) };
        var definition = new MinigameDefinition
        {
            ParametersSchema = Schema(("seed", LuaValueKind.Number)), DefaultParameters = LuaValue.FromJson("{\"seed\":42}"),
            StateSchema = Schema(("deck", LuaValueKind.List), ("matched", LuaValueKind.List), ("first", LuaValueKind.Number), ("second", LuaValueKind.Number),
                ("moves", LuaValueKind.Number), ("pairs", LuaValueKind.Number), ("delay", LuaValueKind.Number), ("started", LuaValueKind.Boolean)),
            InitialState = LuaValue.FromJson("{\"deck\":[],\"matched\":[],\"first\":0,\"second\":0,\"moves\":0,\"pairs\":0,\"delay\":0,\"started\":false}"),
            ResultSchema = Schema(("won", LuaValueKind.Boolean), ("score", LuaValueKind.Number), ("moves", LuaValueKind.Number)),
            UpdatesPerSecond = 5,
        };
        puzzle = puzzle with { Canvas = puzzle.Canvas with { Nodes = nodes.ToImmutable(), HandlerScriptId = script.Id, References = refs.ToImmutable(), Minigame = definition } };
        var story = NovelScene.Create("Пример · запуск пазла") with { Lines = [DialogueLine.Create("ru", "Пазл отменён или завершился с ошибкой. Сюжет продолжился.")] };
        var success = NovelScene.Create("Пример · победа в пазле") with { Lines = [DialogueLine.Create("ru", "Все пары найдены. Структурированный результат выбрал эту ветку сюжета.")] };
        var style = NovellMaker.Core.Styling.DialogueCssParser.Parse(project.DialogueUi.Css);
        story = story with { Canvas = story.Canvas with { Nodes = DialogueCanvas.Add(story.Canvas.Nodes, story.Id, style) } };
        success = success with { Canvas = success.Canvas with { Nodes = DialogueCanvas.Add(success.Canvas.Nodes, success.Id, style) } };
        story = story with { ScriptBlocks = [new(null, ScriptPhase.SceneStart, new StartMinigameAction(Guid.NewGuid(), puzzle.Id, definition.DefaultParameters)),
            new(null, ScriptPhase.SceneStart, new BranchMinigameAction(Guid.NewGuid(), "won", LuaValue.FromBoolean(true), success.Id, ScenarioProgram.LineEntryId(success.Lines[0].Id)))] };
        var added = project with { Scenes = project.Scenes.Add(story).Add(success).Add(puzzle), Scripts = project.Scripts.Add(script) };
        ScreenAsset.ValidateProject(added); ScenarioProgram.Validate(added); return added;
    }

    public const string Source = """
        -- Обычный авторский Lua-ресурс: никакой логики карточек в движке.
        local function paint(s)
          ui.set_text(refs.score,'Ходов: '..s.moves..' · Пар: '..s.pairs..'/4')
          for i=1,8 do
            local node=refs['card'..i]
            ui.set_text(node,(s.matched[i] or s.first==i or s.second==i) and tostring(s.deck[i]) or '?')
            ui.set_enabled(node,not s.matched[i])
          end
        end
        ---@type NMHandlers
        local handlers={}
        handlers.on_load=function()
          local s=ui.get_state()
          if not ui.is_restoring() and not s.started then
            ui.seed_random(ui.minigame_parameters().seed)
            s.deck=ui.list({1,1,2,2,3,3,4,4})
            s.matched=ui.list({false,false,false,false,false,false,false,false})
            for i=8,2,-1 do local j=ui.random(i); local v=s.deck[i]; s.deck[i]=s.deck[j]; s.deck[j]=v end
            s.started=true; ui.set_state(s)
          end
          paint(s)
        end
        ---@param e NMEvent
        handlers.on_event=function(e)
          local s=ui.get_state()
          if e.kind=='service_result' then ui.set_text(refs.status,e.message); return end
          if e.kind=='update' then
            if s.second~=0 then
              s.delay=s.delay+e.seconds
              if s.delay>=0.6 then s.first=0; s.second=0; s.delay=0 end
              ui.set_state(s); paint(s)
            end
            return
          end
          if e.kind~='click' then return end
          if e.node==refs.save then ui.save_slot('card-puzzle','Карточный пазл'); return end
          if e.node==refs.load then ui.load_slot('card-puzzle'); return end
          if e.node==refs.cancel then ui.cancel_minigame(); return end
          if s.second~=0 then return end
          for i=1,8 do
            if e.node==refs['card'..i] and not s.matched[i] and s.first~=i then
              if s.first==0 then s.first=i
              else
                s.moves=s.moves+1
                if s.deck[s.first]==s.deck[i] then
                  s.matched[s.first]=true; s.matched[i]=true; s.pairs=s.pairs+1; s.first=0
                else s.second=i; s.delay=0 end
              end
              ui.set_state(s); paint(s)
              if s.pairs==4 then ui.complete_minigame({won=true,score=math.max(0,100-s.moves),moves=s.moves}) end
              return
            end
          end
        end
        return handlers
        """;
}
