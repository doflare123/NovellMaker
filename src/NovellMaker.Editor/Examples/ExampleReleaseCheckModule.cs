using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable author content; never enables a repository or sends HTTP on load.</summary>
public static class ExampleReleaseCheckModule
{
    public static NovelProject AddTo(NovelProject project)
    {
        var scene = NovelScene.Create("Пример · проверка выпуска");
        var root = scene.Canvas.Nodes[0].Id;
        var status = Guid.NewGuid(); var details = Guid.NewGuid(); var page = Guid.NewGuid(); var check = Guid.NewGuid();
        var cancel = Guid.NewGuid(); var close = Guid.NewGuid(); var probe = Guid.NewGuid(); var input = Guid.NewGuid();
        var script = new LuaScriptAsset(Guid.NewGuid(), "example-release-check.lua", Source);
        scene = scene with { Canvas = scene.Canvas with
        {
            HandlerScriptId = script.Id,
            Nodes = scene.Canvas.Nodes.AddRange(new ScreenNode[] {
                new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(80, 60, 1120, 60), "Проверка выпуска игры"),
                new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(80, 135, 1120, 80), "Репозиторий и версия: параметры проекта → Проверка выпуска игры."),
                new(status, root, ScreenNodeKind.Text, new(80, 235, 1120, 60), "Готово к проверке"),
                new(details, root, ScreenNodeKind.Text, new(80, 300, 1120, 50), "Запрос отправляется только кнопкой. Esc — закрыть меню."),
                new(page, root, ScreenNodeKind.Text, new(80, 360, 1120, 50), ""),
                new(check, root, ScreenNodeKind.Button, new(80, 440, 340, 60), "Проверить новую версию"),
                new(cancel, root, ScreenNodeKind.Button, new(450, 440, 260, 60), "Отменить запрос", Enabled: false),
                new(close, root, ScreenNodeKind.Button, new(740, 440, 300, 60), "Закрыть"),
                new(probe, root, ScreenNodeKind.Button, new(80, 540, 340, 60), "Проверить ввод"),
                new(input, root, ScreenNodeKind.Text, new(450, 540, 740, 60), "Нажатий: 0") }),
            References = ImmutableDictionary<string, Guid>.Empty.Add("status", status).Add("details", details).Add("page", page)
                .Add("check", check).Add("cancel", cancel).Add("probe", probe).Add("input", input),
            Buttons = [new(close, ScreenButtonAction.Close, null)]
        } };
        var result = project with { Scenes = project.Scenes.Add(scene), Scripts = project.Scripts.Add(script),
            InputBindings = project.InputBindings.Where(b => b.Key != "F5")
                .Append(new("F5", ScreenButtonAction.Toggle, scene.Id)).ToImmutableArray() };
        ScreenAsset.ValidateProject(result); return result;
    }

    public const string Source = """
        -- Настройте свой публичный репозиторий в параметрах проекта.
        -- Замыкания живут до закрытия меню; сеть и Lua-стек не сохраняются.
        local request = nil
        local clicks = 0
        return {
          on_event = function(e)
            if e.kind == 'click' then
              if e.node == refs.check and not request then
                request = ui.check_game_release()
                ui.set_enabled(refs.check, false)
                ui.set_enabled(refs.cancel, true)
                ui.set_text(refs.status, 'Проверка…')
                ui.set_text(refs.details, 'Ожидание ответа. Ввод и отрисовка продолжаются.')
                ui.set_text(refs.page, '')
              elseif e.node == refs.cancel and request then
                ui.cancel_release_check(request)
                ui.set_enabled(refs.cancel, false)
                ui.set_text(refs.status, 'Отмена…')
              elseif e.node == refs.probe then
                clicks = clicks + 1
                ui.set_text(refs.input, 'Нажатий: '..clicks)
              end
            elseif e.kind == 'service_result' and e.operation == 'CheckGameRelease' and e.request_id == request then
              request = nil
              ui.set_enabled(refs.check, true)
              ui.set_enabled(refs.cancel, false)
              if e.success then
                local r = e.release
                ui.set_text(refs.status, r.update_available and 'Доступна новая версия' or 'Новая версия не требуется')
                ui.set_text(refs.details, 'Установлена: '..r.current_version..' · Выпуск: '..r.latest_version..(r.from_cache and ' · Из кэша' or ''))
                ui.set_text(refs.page, r.page_url)
              elseif e.code == 'Cancelled' then
                ui.set_text(refs.status, 'Проверка отменена')
                ui.set_text(refs.details, e.message)
              else
                ui.set_text(refs.status, 'Ошибка проверки: '..e.code)
                ui.set_text(refs.details, e.message..(e.retry_after_seconds and (' Повтор через '..e.retry_after_seconds..' с.') or ''))
              end
            end
          end
        }
        """;
}
