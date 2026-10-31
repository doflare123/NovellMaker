# События и структуры результатов

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [Ввод](input-and-triggers.md)

События получает `on_event(e)` обработчика **ui** или авторская функция модуля, которой обработчик передал e. Сценарная VM не получает эту таблицу. on_load получает `{}`; `kind="load"` не существует. Нет произвольных подписок/callbacks хоста: поле `on_event` обязательно, `on_load` необязательно, обе — Lua function.

## Общая структура и доставка

Для **каждого** on_event обязательны:

| Поле | Тип | Смысл |
| --- | --- | --- |
| kind | string | одно из значений ниже |
| x, y | number | логические пиксели viewport 1280×720; для некоординатного события 0 |
| seconds | number | игровая дельта [0,1]; обычно 0, у update — накопленная дельта |
| node | handle или nil | цель ввода; у служебных результатов/trigger/update отсутствует |

Отсутствующее поле Lua читается как nil, это не `ui.null`. Координаты протокола конечны и ограничены модулем 100000. Shift/control/alt существуют только у key_down/key_up; статус/ID/сообщения — только у соответствующего результата. Внутренние epoch/sequence/origin/generation/depth в Lua не проецируются.

Очередь одна на ScreenLuaSession, до 128 событий **вместе с резервами** ответов сервисов, групп анимаций и звуков. Исполнение последовательно; событие содержит снимок сюжетных переменных на момент принятия, который читает ui.get. У ui.get_state/RNG — последнее подтверждённое состояние экземпляра перед исполнением. Загрузка обработчика обязана предшествовать его событиям.

Порядок обычной очереди FIFO. Только соседние `move`, `hover` или `drag` **одного kind/экземпляра/node** объединяются заменой последним; через click/другое значимое событие не переставляются. Завершённые сервисные ответы вставляются перед обычным вводом в порядке принятия ответов, после обязательных load. Анимационные/звуковые результаты и триггеры добавляются в хвост с заранее учтёнными резервами/лимитами; для них отдельного глобального приоритета нет. Управляющее закрытие VM обрабатывается раньше обычной очереди.

Порождённые trigger/service/animation/audio/input-цепочки ограничены 32 поколениями; продолжение сервиса не обнуляет глубину. Переполнение квоты при подготовке отклоняет пакет без частичного эффекта. Переполненный физический ввод останавливает соответствующий обработчик с диагностикой. Устаревшие click/события другой эпохи могут быть пропущены, а не переадресованы новому экрану.

Общий срок события — 5 секунд от принятия, включая очередь/подготовку/IPC. При приостановке владельца его очередь не исполняется; срок ожидающих событий сдвигается на паузу. Готовые ответы удерживаются и не блокируют работу меню. Уже отправленная работа не получает бесконечного продления native deadline. Истечение до отправки обычного события даёт диагностику; истечение load/физического input/сервисного/анимационного/звукового результата отключает обработчик, чтобы не оставлять вечный busy. Сбой IPC может отключить всю беседу. Закрытие/Reset/успешный Load удаляют старые события и резервы; поздние ответы не попадают в новый экземпляр.

Источник: [проверка и проекция](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs), [Lua-поля](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [очередь](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.cs). Тесты: [протокол](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaProtocolTests.cs), [порядок/пауза/сроки](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaSessionTests.cs).

## Ввод: все kind

Ниже поля дополняют общую структуру. В событиях с узлом `node` — handle исходного экземпляра; он может уже быть удалён только у оговорённых уведомлений очистки. Это не разрешает снова изменять его через ui.

| kind | Источник и обязательные/условные поля | Порядок и условия |
| --- | --- | --- |
| click | успешная активация Button/Area; node обязателен, x=y=seconds=0 в текущем runtime | мышь отпущена над исходной целью без drag либо Activate с клавиатуры; действие кнопки должно быть Lua; без input_events тоже доступен |
| move | PointerMove; node — текущая цель/capture, x/y — координаты указателя | при реальном изменении позиции; соседние объединяются; физическая доставка требует input_events=true |
| hover | протокол допускает координатное уведомление, node необязателен | текущий RuntimeScreens самостоятельно его не создаёт; не используйте как обязательное событие hover-цикла, используйте enter/leave |
| pointer_down | нажатие по доступному Button/Area; node,x,y | сначала смена focus (blur/focus), затем pointer_down; автоматический исходный capture |
| pointer_up | отпускание захваченной последовательности; node,x,y | сначала pointer_up, затем drag_end при drag, затем click при допустимой активации без drag |
| pointer_cancel | очистка capture; node, x/y обычно 0 | release_pointer, смена модальности/фокуса/доступности; synthetic click не создаёт; node может быть retired |
| drag_start | движение при capture, node,x,y | при Manhattan distance от исходного нажатия >=4 логических пикселя; непосредственно перед первым drag |
| drag | движение при активном drag; node,x,y | может выходить за viewport при capture; соседние объединяются |
| drag_end | отпускание в drag; node,x,y | после pointer_up; click этой последовательности не создаётся |
| hover_enter | смена hover на доступную цель; node,x,y | старой цели сначала hover_leave, новой — hover_enter, затем move |
| hover_leave | уход/очистка hover; node,x,y | node может быть retired; при очистке координаты могут быть 0 |
| focus | новый клавиатурный фокус; node, x=y=0 | после blur старого; только при изменении цели |
| blur | потеря фокуса; node, x=y=0 | старый node может быть retired |
| key_down | физическая клавиша; node,key:string,shift/control/alt:boolean | цель — focus либо корень верхнего слоя; повторы удерживаемой клавиши подавлены |
| key_up | отпускание принятой клавиши; те же key/modifiers | принадлежит исходной цели нажатия; без принятого down не доставляется |
| input_reset | сброс удерживаемых клавиш; node — корень владельца, x=y=0 | при очистке/потере фокуса/удалении цели; key и modifiers отсутствуют, очистите собственные held-флаги |

Все физические события, кроме совместимого click, требуют `input_events=true`. Протокол допускает `move/hover` без node, но текущая маршрутизация move направляет его живой цели. Никакого `button`, `local_x`, `local_y`, `wheel`, `text`, `repeat`, `pointer_id` в проекции нет. Мышь не получает выдуманный key. Ввод не всплывает авторской цепочкой по родителям.

Допустимые key: `A`..`Z`, `F1`..`F12`, `D0`..`D9`, `Escape`, `Enter`, `Space`, `Tab`, `Left`, `Right`, `Up`, `Down`, `Back`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, `LeftShift`, `RightShift`, `LeftControl`, `RightControl`, `LeftAlt`, `RightAlt`. Это перечень протокола; событие получит только клавиша, которую host передал маршрутизатору. Назначения действий проекта имеют более узкий набор (без D0..D9 и дополнительных служебных клавиш).

```lua
return {input_events=true, on_event=function(e)
  if e.kind == "key_down" and e.key == "Space" then
    ui.set_text(refs.label, e.shift and "Shift+Space" or "Space")
  elseif e.kind == "input_reset" then
    ui.set_text(refs.label, "Клавиши отпущены")
  end
end}
```

Нужен Text refs.label и активный экземпляр. Пример показывает физическую клавишу, не текстовый ввод. Для drag используйте сохранённые координаты/состояние и осторожно преобразуйте viewport в локальную систему; API обратной матрицы в Lua нет. Нельзя считать e.x/e.y локальными координатами вложенного/повёрнутого узла.

Источник: [маршрутизатор](../../src/NovellMaker.Core/Screens/ScreenStack.Input.cs), [stack](../../src/NovellMaker.Core/Screens/ScreenStack.cs), [runtime dispatch](../../src/NovellMaker.Runtime/RuntimeScreens.cs), [keys](../../src/NovellMaker.Core/Screens/ProjectInputBinding.cs). Тесты: [ввод](../../tests/NovellMaker.Core.Tests/Screens/ExtendedInputTests.cs), [runtime](../../tests/NovellMaker.Runtime.Tests/ExtendedInputRuntimeTests.cs).

### Обработчик каждого вида ввода

Полный диагностический обработчик ниже показывает реакцию на каждое имя из таблицы. Нужны неизменяемый Text refs.label и хотя бы одна доступная кнопка/Area с действием Lua. Обработчик не хранит игровой input-state; для своей механики отдельно очищайте held-флаги при cancel/reset. hover включён для полноты протокола, текущий runtime его не порождает.

```lua
local function show(text) ui.set_text(refs.label, text) end
local function position(e) return e.kind..":"..e.x..","..e.y end
local dispatch={
  click=function(e) show("click") end,
  move=function(e) show(position(e)) end,
  hover=function(e) show(position(e)) end,
  pointer_down=function(e)
    ui.capture_pointer(e.node)
    show(position(e))
  end,
  pointer_up=function(e) show(position(e)) end,
  pointer_cancel=function(e) show("pointer_cancel") end,
  drag_start=function(e) show(position(e)) end,
  drag=function(e) show(position(e)) end,
  drag_end=function(e) show(position(e)) end,
  hover_enter=function(e) show(position(e)) end,
  hover_leave=function(e) show(position(e)) end,
  focus=function(e) show("focus") end,
  blur=function(e) show("blur") end,
  key_down=function(e) show("down:"..e.key) end,
  key_up=function(e) show("up:"..e.key) end,
  input_reset=function(e) show("input_reset") end
}
return {input_events=true, on_event=function(e)
  local handler=dispatch[e.kind]
  if handler then handler(e) end
end}
```

Это иллюстрация маршрутизации, не обещание появления каждого события при каждом действии. Например, drag не создаётся без press+движения, а отмена последовательности не создаёт click. Все команды примера принимаются обычными пакетами.

## update

`{kind="update", seconds:number, x=0, y=0}`, node отсутствует. В runtime источник — **Running мини-игра** с UpdatesPerSecond 1..20, default 5. Накопленная дельта ограничивается 1 секундой. Пока обработчик занят, время накапливается до этого предела; когда экземпляр settled и достигнут интервал, принимается одно событие, аккумулятор сбрасывается. Это не гарантия воспроизвести каждый потерянный шаг и не системные часы.

При паузе мини-игра не получает игровые update; on_event не должен блокироваться в ожидании следующего. Для детерминированной логики храните прошедшее время в state, а не используйте реальные часы (они недоступны).

```lua
return {on_event=function(e)
  if e.kind == "update" then
    local s=ui.get_state() -- StateSchema: {elapsed:Number}, InitialState={elapsed=0}
    s.elapsed=s.elapsed+e.seconds
    ui.set_state(s)
  end
end}
```

Источник: [TickMinigame](../../src/NovellMaker.Runtime/RuntimeScreens.Minigames.cs). Тесты: [runtime screens](../../tests/NovellMaker.Runtime.Tests/RuntimeScreensTests.cs), [мини-игры](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).

## trigger

`{kind="trigger",trigger_id:string,trigger_data?:string,x=0,y=0,seconds=0}`. trigger_id — обязательный AuthorKey; trigger_data отсутствует для nil или строка <=1024 (пустая отличается от nil). node отсутствует. Источник текущего авторского runtime — принятый `ui.trigger`. Отдельного штатного действия кнопки Trigger нет: назначьте Lua и вызовите ui.trigger в обработчике click. Получатель — тот же экземпляр, не все сцены.

После commit отправителя события добавляются в хвост в порядке вызовов. До 16 триггеров/пакет, 128 общая очередь, глубина 32. Нет отложенного возврата в вызвавшую функцию; при закрытии/смене сеанса не сохраняются.

```lua
return {on_event=function(e)
  if e.kind=="click" and e.node==refs.button then
    ui.trigger("door.open", "key")
  elseif e.kind=="trigger" and e.trigger_id=="door.open" then
    ui.set_text(refs.label, e.trigger_data or "без данных")
  end
end}
```

Источник: [AuthorTrigger](../../src/NovellMaker.Core/Triggers/AuthorTrigger.cs), [session](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.cs). Тесты: [PersistentDataLuaTests](../../tests/NovellMaker.Core.Tests/Screens/PersistentDataLuaTests.cs).

<a id="animation-result"></a>
## animation_result

Обязательные поля: `kind="animation_result"`, `animation_id:string` GUID N из ui.animate, `status:string`, `reason:string`, x=y=seconds=0. node отсутствует. Один результат на принятую группу при живом владельце; ID описания tween не используется для корреляции.

| status | reason, которые создаёт host | Условие |
| --- | --- | --- |
| completed | finished | достигнут конец timeline |
| cancelled | requested | cancel_animation своего живого ID |
| cancelled | replaced | новая группа пересекается по узлу/свойству |
| cancelled | property_set | set_geometry затронул анимируемое свойство |
| cancelled | removed | удалён узел трека |
| cancelled | reparented | set_parent в экземпляре |
| failed | invalid_geometry | host не смог построить допустимый кадр |

Протокол ограничивает reason длиной 128, а не enum; выше полный список значений **производителя текущего host**. Результат сохраняет причинную глубину, резервируется до старта. Пауза задерживает timeline/доставку; закрытие/Reset очищает их без доставки в новый экземпляр. Неизвестный ID cancel_animation — no-op и не создаёт событие.

```lua
return {on_load=function()
  ui.animate(ui.tween(refs.label, {x=200}, 1))
end, on_event=function(e)
  if e.kind=="animation_result" then
    ui.set_text(refs.label, e.status..":"..e.reason)
  end
end}
```

Источник: [ScreenAnimator](../../src/NovellMaker.Core/Screens/Animations/ScreenAnimation.cs). Тесты: [AnimationTests](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).

<a id="audio-result"></a>
## audio_result

Обязательные поля: `kind="audio_result"`, `audio_id:string` GUID N из play_audio, `status:string`, `message:string` <=1024, x=y=seconds=0. node отсутствует. status **только** `completed`, `stopped`, `failed`; `cancelled` для звука нет. message может быть пустой, машинного code/reason у аудио нет.

completed — естественный конец незацикленного звука; stopped — явная остановка; failed — ошибка открытия/формата/backend/лимита. Loop сам по себе не заканчивается. Ticket удаляется после принятия обработчика результата: не храните ID как действующий после этого. Повторный stop ещё учтённого завершённого источника не создаёт второй результат. StopOwner при закрытии очищает владельца, callback в исчезнувшую VM не обещан.

Результат получает исходный владелец, резерв очереди создаётся при play. Пауза и pending input/result удерживают аудио; pending обычного update — исключение, см. [звук](audio.md). Перед сохранением мини-игры должны закончиться и звук, и обработка его результата.

```lua
return {on_load=function()
  ui.play_audio(audio.click, "effect", false, 0.7)
end, on_event=function(e)
  if e.kind=="audio_result" then
    ui.set_text(refs.label, e.status)
    if e.status=="failed" then ui.log("warning", e.message) end
  end
end}
```

Нужны WAV-ссылка audio.click и Text refs.label. Источники: [команды/результаты](../../src/NovellMaker.Core/Audio/ScriptAudio.cs), [host tickets](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.Audio.cs), [backend admission](../../src/NovellMaker.Core/Audio/AudioService.cs). Тесты: [ScriptAudioLuaTests](../../tests/NovellMaker.Core.Tests/Screens/ScriptAudioLuaTests.cs).

<a id="service-result"></a>
## service_result

Обязательные поля каждого сервисного ответа:

| Поле | Тип | Значение |
| --- | --- | --- |
| kind | string | service_result |
| request_id | string | GUID N, возвращённый сервисным ui-вызовом |
| operation | string | точное имя операции из таблицы ниже |
| success | boolean | успех операции, отдельно от persisted |
| message | string | пояснение <=1024, не машинный код |
| slots | плотный список | есть всегда; пуст кроме успешного ListSlots |
| x,y,seconds | number | 0 |

node отсутствует. `slot_id`, `settings`, `persisted`, `key`, `value`, release-поля условны:

| operation | Дополнительные поля и доставка |
| --- | --- |
| NewGame | при отказе только общие; при успешной смене lifetime старому обработчику ответа нет |
| MainMenu | как NewGame; требуется назначенное меню и Playing |
| EndSession | при успехе окно/владелец завершены, callback нет |
| ContinueGame | отказ — событие; успешный Load меняет lifetime, callback старому нет |
| ListSlots | slots до 128; при ошибке пустой список |
| SaveSlot | slot_id — фактический созданный/переданный ID, если он назначен; успех после публикации |
| LoadSlot | slot_id запроса; отказ приходит, успех сменяет lifetime без callback старому |
| DeleteSlot | slot_id запроса, даже при отказе |
| GetSettings | settings при успехе (runtime может вернуть и при отказе), persisted всегда boolean |
| SetSetting | settings текущей памяти, persisted всегда boolean, в том числе при ошибке записи |
| GetPersistent | key всегда, value строка либо nil, persisted всегда boolean |
| SetPersistent | как GetPersistent, value — действующая память после попытки операции |
| CheckGameRelease | provider/code обязательны, retry_after_seconds/release условны; см. ниже |

`CancelReleaseCheck` **не доставляется** как operation собственного ответа. Отмена завершается ответом исходного CheckGameRelease.

Результаты коррелируются одновременно по request_id, operation и исходному живому экземпляру/поколению. Повторные/чужие/устаревшие отбрасываются. Для сериализуемых вызовов одного обработчика сохраняйте ID в local или state и проверяйте его; не путайте request_id с ID слота. При успешном разрушении источника нельзя ждать положительный callback, чтобы обновить его label.

```lua
local pending
return {on_load=function()
  pending=ui.list_slots()
end, on_event=function(e)
  if e.kind=="service_result" and e.request_id==pending then
    pending=nil
    if e.success then
      ui.set_text(refs.label, "Слотов: "..#e.slots)
    else
      ui.set_text(refs.label, e.message)
    end
  end
end}
```

### Элемент slots

Всегда: `id:string`, `status:string`, `compatible:boolean` (status==Compatible), `legacy:boolean`. `diagnostic?:string` <=1024 — пояснение, если есть. `status` — **Compatible, ForeignProject, IncompatibleRevision, UnsupportedVersion, Corrupt, Unavailable**. Строки несовместимых/повреждённых слотов не являются разрешением загрузки.

При наличии проверенных metadata: `title:string` <=128, `scene:string` — имя <=256, `phrase:string` <=512, `revision:string` <=128, `saved_at:string` UTC ISO 8601 формата O, `game_time:number` конечное >=0 секунд. Это имена/текст, не GUID сцены/фразы; отдельные sceneId/lineId в Lua не проецируются. Compatible требует metadata. При превышении бюджета IPC отображаемые title/scene/phrase/diagnostic сокращаются и message сообщает об этом; количество/ID/статусы/редакции/время не усекаются. Список отсортирован по SavedAtUtc убыванию, при равенстве по ID ordinal.

### settings и persisted

settings — таблица ровно с доступными полями: `master_volume`, `sound_volume`, `effect_volume`, `voice_volume` — number [0,1]; `master_muted`, `sound_muted`, `effect_muted`, `voice_muted`, `fullscreen` — boolean. Изменение этой таблицы само профиль не меняет.

persisted есть только у четырёх операций профиля. Это отсутствие неподтверждённых изменений **всего** профиля, а не «данный ключ существует» и не повтор success. После отказа диска success=false, но value/settings могут быть новыми в памяти. `value=nil` — отсутствие ключа; `value=""` — существующая пустая строка. У операций слотов/settings/проверки выпуска нет произвольных полей Data; у непрофильных операций persisted отсутствует.

Источник: [команда/схема ответа](../../src/NovellMaker.Core/Execution/GameServiceCommand.cs), [Lua-проекция](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [lifetime bridge](../../src/NovellMaker.Runtime/RuntimeGameServices.cs), [профиль](../../src/NovellMaker.Runtime/RuntimeSettingsServices.cs), [список](../../src/NovellMaker.Core/Execution/GameSlotStore.cs). Тесты: [сервисы](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs), [слоты](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs), [runtime](../../tests/NovellMaker.Runtime.Tests/RuntimeSettingsServicesTests.cs).

<a id="release-result"></a>
## CheckGameRelease: расширение service_result

Дополнительно всегда `provider="github_releases_v1"`, `code:string` из таблицы; success=true **только** для Ok. `retry_after_seconds:integer>0` есть **только** при RateLimited; это подсказка для следующего разрешённого запроса, не автоматический retry. `release:table` есть **только** при Ok. slots пуст, slot_id/settings/persisted/key/value отсутствуют.

| code | Значение |
| --- | --- |
| Ok | проверенные данные получены из сети или кеша |
| Disabled | в проекте не настроен провайдер |
| Busy | владелец уже занят либо очередь заполнена |
| RateLimited | действует локальный интервал или ограничение провайдера |
| Cancelled | исходный запрос отменён автором при живом владельце |
| Timeout | истекли 10 секунд запроса с ожиданием |
| NetworkUnavailable | ошибка сети/чтения ответа |
| TlsFailure | ошибка защищённого соединения |
| NotFound | нет публичного репозитория/полного выпуска, HTTP 404 |
| AccessDenied | HTTP 401/403 без признанного rate limit |
| ProviderUnavailable | HTTP 5xx |
| InvalidResponse | неверный статус/формат/тип/повторный JSON ключ/URL/UTF-8 и т.п. |
| ResponseTooLarge | заголовки/распакованное тело/данные превышают лимит |
| InvalidVersion | тег версии не поддерживается |

release содержит все поля: `current_version:string`, `latest_version:string` — канонические четыре ushort-компонента через точки; `update_available:boolean` — latest>current; `page_url:string` <=512 — проверенный HTTPS GitHub URL той же игры; `checked_at:string` UTC ISO 8601; `from_cache:boolean`. URL — данные, не открывается и не выполняется. Системная дата используется host для метаданных, не даёт Lua часы.

```lua
return {on_load=function() ui.check_game_release() end,
  on_event=function(e)
    if e.kind=="service_result" and e.operation=="CheckGameRelease" then
      if e.code=="Ok" then
        ui.set_text(refs.label, e.release.latest_version)
      elseif e.code=="RateLimited" then
        ui.set_text(refs.label, "Повторите через "..e.retry_after_seconds.." с")
      else
        ui.set_text(refs.label, e.message)
      end
    end
  end}
```

Требуется декларация сервиса и refs.label; при отсутствии декларации обрабатывается Disabled. Не помещайте повторный check прямо в каждый service_result: причинная цепочка ограничена 32 и интервал HTTP не позволяет polling-шторм. Закрытие/загрузка отменяет старый источник без доставки в новую VM. Пауза задерживает callback, но не сеть/deadline/кеш.

Источник: [коды/результат](../../src/NovellMaker.Core/ExternalServices/ReleaseCheck.cs), [service](../../src/NovellMaker.Core/ExternalServices/ReleaseCheckService.cs), [адаптер](../../src/NovellMaker.Runtime/ExternalServices/GitHubReleaseProvider.cs). Тесты: [Lua](../../tests/NovellMaker.Core.Tests/Screens/ReleaseCheckLuaTests.cs), [HTTP](../../tests/NovellMaker.Runtime.Tests/ReleaseCheckProviderTests.cs).

## Результат мини-игры в сценарии

Это **не ui-событие**: следующая game-VM читает `game.minigame_result() -> {status,data}|nil`. status: `completed`, `cancelled`, `failed`. При completed data соответствует ResultSchema, при двух других — null-маркер. Message, SceneId и RunId host сохраняет внутренне, но Lua-функция их не возвращает. `game.start_minigame` очищает прошлый итог при исполнении; итог затем хранится в GameState/слоте. Старое строковое `game.finish` от него независимо.

Пример: [мини-игра и следующий ресурс](examples.md#minigame). Источники: [проекция](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [lifecycle](../../src/NovellMaker.Core/Execution/ScenarioPlayer.Minigames.cs). Тесты: [MinigameTests](../../tests/NovellMaker.Core.Tests/Execution/MinigameTests.cs).
