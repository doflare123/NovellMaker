# Законченные небольшие примеры

[Оглавление](index.md) · [Модель исполнения](execution-model.md)

Каждый именованный блок ниже — полный исходник отдельного Lua-блока/ресурса. Имена файлов — авторские названия; физический путь ресурса редактор назначит по ID. Создайте ресурсы через GUI, задайте ссылки и назначьте обработчик холсту. Для click кнопка/область должна иметь действие **Lua**. Все refs — собственные узлы одного холста. Тесты читают именно эти блоки из Markdown, а не отдельные копии примеров: [LuaReferenceExamplesTests](../../tests/NovellMaker.Core.Tests/Screens/LuaReferenceExamplesTests.cs). Степень проверки каждого примера указана в [verification](verification.md).

<a id="queue"></a>
## Очередь сценария и следующий блок

Ресурсы и refs не нужны. Поместите в «Начало сцены» Lua-блок:

<!-- example:scenario-queue -->
```lua
game.set("stage", "started")
game.wait(1)
game.set("stage", "done")
game.wait_input()
```

После принятия Lua stage равно started. Через одну **игровую** секунду станет done, затем очередь ждёт продолжения. Lua целиком вычислился до секундного ожидания; local-переменная внутри этого блока не становится возобновляемым стеком.

## Обработчик счётчика

На холсте Text `label` и Button `add`; назначьте эти aliases в ссылках холста. Схема мини-игры не нужна, это обычный экран. Он начинает с нуля при новом открытии, а не хранит счётчик в игровом слоте.

<!-- example:counter -->
```lua
local function render(s)
  ui.set_text(refs.label, "Нажатий: "..s.count)
end
return {
  on_load=function()
    local s=ui.get_state()
    if s==ui.null then
      s={count=0}
      ui.set_state(s)
    end
    render(s)
  end,
  on_event=function(e)
    if e.kind=="click" and e.node==refs.add then
      local s=ui.get_state()
      s.count=s.count+1
      ui.set_state(s)
      render(s)
    end
  end
}
```

После каждого click и полного commit увеличиваются state.count и надпись. Изменить только `s.count` без set_state недостаточно. После закрытия экран теряет это состояние.

## Импорт модуля

Создайте Lua-ресурс `counter-helper`. В его References ничего не требуется:

<!-- example:module-helper -->
```lua
return {
  caption=function(count)
    return "Счёт: "..tostring(math.floor(count))
  end
}
```

У корневого ресурса объявите ссылку **helper** на counter-helper; на холсте — refs.label Text. Корневой ресурс назначьте обработчиком:

<!-- example:module-root -->
```lua
local helper=ui.import(modules.helper)
return {
  on_load=function()
    ui.set_text(refs.label, helper.caption(2.8))
  end,
  on_event=function(e) end
}
```

Получится «Счёт: 2». caption — авторская функция этого модуля, не встроенный API. У модуля свои globals/библиотеки и нет refs.label. Для изменения узла внутри модуля передайте handle аргументом.

## Воспроизводимая случайность

Нужен Text refs.label; это обычный экран, сохранение в слот не подразумевается:

<!-- example:random-checkpoint -->
```lua
return {
  on_load=function()
    ui.seed_random(42)
    local checkpoint=ui.get_random_state()
    local first=ui.random(1,100)
    ui.set_random_state(checkpoint)
    local replay=ui.random(1,100)
    ui.set_state({first=first,replay=replay})
    ui.set_text(refs.label, first.." / "..replay)
  end,
  on_event=function(e) end
}
```

Оба числа совпадут, потому что восстановлен точный поток до выборки. Сбрасывать seed каждый update нельзя: это будет начало одного и того же потока. Для сохраняемой мини-игры RNG хранится host вместе со state; не нужно повторно seed в восстанавливающем on_load.

<a id="minigame"></a>
## Сохраняемая мини-игра с результатом

Создайте сцену `puzzle` с Text refs.label и Button refs.add, включите мини-игру. Параметры:

| Настройка | Значение |
| --- | --- |
| ParametersSchema | Object, поле target:Number |
| DefaultParameters | `{ "target": 3 }` |
| StateSchema | Object, поле score:Number |
| InitialState | `{ "score": 0 }` |
| ResultSchema | Object, поле score:Number |
| CanSave | true |
| UpdatesPerSecond | 5; пример update не использует |

Назначьте обработчик:

<!-- example:minigame-handler -->
```lua
local function render(s)
  ui.set_text(refs.label, "Счёт: "..s.score)
end
return {
  on_load=function()
    -- И новый, и восстановленный экземпляр получают готовые данные host.
    -- Здесь только восстанавливается вид, без побочных операций.
    render(ui.get_state())
  end,
  on_event=function(e)
    if e.kind=="click" and e.node==refs.add then
      local s=ui.get_state()
      local p=ui.minigame_parameters()
      s.score=s.score+1
      ui.set_state(s)
      render(s)
      if s.score>=p.target then
        ui.complete_minigame({score=s.score})
      end
    end
  end
}
```

Сохраните прохождение между нажатиями, когда нет pending ввода/обработчика/операций. При восстановлении надпись строится из сохранённого score; следующий click продолжит счёт. В on_load намеренно нет нового seed/trigger/сервиса. При третьем click мини-игра закроется с completed и `{score=3}`. Закрытие её экрана другим способом даст cancelled.

Создайте **сценарный** ресурс after-puzzle:

<!-- example:minigame-after -->
```lua
local result=game.minigame_result()
if result and result.status=="completed" then
  if result.data.score>=3 then
    game.set("puzzle.won", "true")
  end
else
  game.set("puzzle.won", "false")
end
```

В сценарном блоке сцены объявите refs.puzzle на сцену мини-игры и refs.after на after-puzzle. Запуск:

<!-- example:minigame-launch -->
```lua
game.start_minigame(refs.puzzle, {target=3})
game.run(refs.after)
```

after исполнится после завершения барьера с новым снимком, а не сразу при вычислении launch. Его `return` ничего не передаёт назад. Для ветвления по puzzle.won используйте GUI-блок условия или следующий Lua-блок с jump_line к выбранным refs.scene/refs.line.

## Постоянная отметка с подтверждением диска

Нужны Button refs.mark и Text refs.label. Ни внешний сервис, ни schema не требуются. Профиль принадлежит проекту, чтение не означает подтверждение записи:

<!-- example:profile -->
```lua
local pending
return {
  on_load=function()
    pending=ui.get_persistent("achievement.first")
  end,
  on_event=function(e)
    if e.kind=="click" and e.node==refs.mark and pending==nil then
      pending=ui.set_persistent("achievement.first", "true")
    elseif e.kind=="service_result" and e.request_id==pending then
      pending=nil
      if not e.success then
        ui.set_text(refs.label, "Не записано: "..e.message)
      elseif e.value=="true" then
        ui.set_text(refs.label, e.persisted and "Подтверждено" or "Только в памяти")
      else
        ui.set_text(refs.label, "Ещё не отмечено")
      end
    end
  end
}
```

Один сервис на пакет; обработчик результата не вызывает второй бесконечный запрос. При ошибке записи можно снова нажать mark после ответа: то же значение повторит запись. Эта отметка — ваша логика, платформенных achievements API нет.

## Анимация без цикла Lua

Text refs.label должен быть видимым, его конечное x=200 допустимо геометрией. Обычный экран не нуждается в update:

<!-- example:animation -->
```lua
local animation
return {
  on_load=function()
    animation=ui.animate(ui.sequence({
      ui.tween(refs.label, {x=200}, 0.5, "out_quad"),
      ui.tween(refs.label, {opacity=0.5}, 0.5)
    }))
  end,
  on_event=function(e)
    if e.kind=="animation_result" and e.animation_id==animation then
      animation=nil
      ui.set_text(refs.label, e.status..":"..e.reason)
    end
  end
}
```

Через одну активную игровую секунду x=200 и opacity=0.5; надпись completed:finished. Анимация идёт на host, Lua стек между кадрами не исполняется. Покрывающая пауза останавливает timeline.

## Свой WAV и обработка ошибки

Импортируйте PCM WAV, добавьте в References корневого Lua-ресурса alias click. На холсте Text refs.label. Профильные громкости перемножаются с 0.7:

<!-- example:audio -->
```lua
local source
return {
  on_load=function()
    source=ui.play_audio(audio.click, "effect", false, 0.7)
  end,
  on_event=function(e)
    if e.kind=="audio_result" and e.audio_id==source then
      source=nil
      ui.set_text(refs.label, e.status)
      if e.status=="failed" then ui.log("warning", e.message) end
    end
  end
}
```

При естественном окончании — completed; при host-ошибке — failed и диагностическое сообщение. Повторно использовать source после его результата нельзя. Не назначайте этот on_load восстанавливаемой мини-игре: запуск звука при restore запрещён.

## Проверка версии с отменой

В параметрах проекта задайте GitHub Releases: публичные Owner/Repository и CurrentVersion. Кнопки refs.check/refs.cancel и Text refs.label; при отсутствии декларации запрос безопасно вернёт Disabled. Ни URL, ни токен в Lua не передаются.

<!-- example:release -->
```lua
local pending
return {
  on_event=function(e)
    if e.kind=="click" and e.node==refs.check and pending==nil then
      pending=ui.check_game_release()
      ui.set_text(refs.label, "Проверяем...")
    elseif e.kind=="click" and e.node==refs.cancel and pending~=nil then
      ui.cancel_release_check(pending)
    elseif e.kind=="service_result" and e.operation=="CheckGameRelease"
        and e.request_id==pending then
      pending=nil
      if e.code=="Ok" then
        ui.set_text(refs.label, e.release.latest_version)
      elseif e.code=="RateLimited" then
        ui.set_text(refs.label, "Повтор через "..e.retry_after_seconds.." с")
      else
        ui.set_text(refs.label, e.message)
      end
    end
  end
}
```

Нет автообновления файлов или открытия page_url. Cancel не возвращает второго request ID; результат приходит на исходный pending. Повторный check делается только по новому нажатию и ограничениям сервиса.
