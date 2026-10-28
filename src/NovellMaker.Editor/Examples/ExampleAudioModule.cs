using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Uses an author-imported WAV. The project owns all code, references and UI.</summary>
public static class ExampleAudioModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var asset = project.Audio.Assets.FirstOrDefault() ?? throw new ArgumentException("Сначала импортируйте PCM WAV на вкладке «Звук».");
        var added = ExampleComponentStateModule.AddTo(project);
        var script = added.Scripts.Last() with { Name = "example-audio.lua", Source = Source,
            References = ImmutableDictionary<string, Guid>.Empty.Add("sample", asset.Id) };
        var scene = added.Scenes.Last() with { Name = "Пример · звук Lua" };
        var nodes = scene.Canvas.Nodes.Select(n => n.Name switch
        {
            "title" => n with { Text = "Звук из Lua" },
            "description" => n with { Text = "Первый импортированный WAV · источник принадлежит этому окну · закрытие останавливает звук" },
            "state" => n with { Text = "Готово" },
            "increment" => n with { Text = "Играть / громче" },
            "reset" => n with { Text = "Остановить" },
            _ => n
        }).ToImmutableArray();
        scene = scene with { Canvas = scene.Canvas with { Nodes = nodes } };
        return added with { Scenes = added.Scenes.SetItem(added.Scenes.Length - 1, scene), Scripts = added.Scripts.SetItem(added.Scripts.Length - 1, script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F10").Append(new("F10", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
    }

    public const string Source = """
        -- audio.sample выбирается в именованных ссылках Lua-ресурса; путь не нужен.
        -- Громкость источника умножается на Effect и Master, профиль не меняется.
        local playing=nil
        local volume=0.25
        ---@type NMHandlers
        local handlers={}
        handlers.on_load=function() ui.set_text(refs.state,'Готово') end
        ---@param e NMEvent
        handlers.on_event=function(e)
          if e.kind=='click' and e.node==refs.increment then
            if playing==nil then
              volume=0.25
              playing=ui.play_audio(audio.sample,'effect',true,volume)
            else
              volume=math.min(1,volume+0.25)
              ui.set_audio_volume(playing,volume)
            end
            ui.set_text(refs.state,'Громкость: '..volume)
          elseif e.kind=='click' and e.node==refs.reset and playing~=nil then
            ui.stop_audio(playing)
          elseif e.kind=='audio_result' and e.audio_id==playing then
            ui.set_text(refs.state,e.status=='failed' and 'Ошибка: '..e.message or e.status)
            playing=nil
          end
        end
        return handlers
        """;
}
