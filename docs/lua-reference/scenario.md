# Сценарный API game.*

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Все функции этой страницы доступны **только** в сценарном блоке или ресурсе, исполняемом `game.run`. Сценарный ресурс не получает `ui`, `modules`, `audio`, `math`, `string`, `table` или глобальные функции библиотеки. В обработчике/импортируемом модуле `game` отсутствует.

Общие правила каждой записи: [вычисление и очередь](execution-model.md#scenario), [сроки жизни и загрузка](execution-model.md#lifetime), [квоты](limits.md#scenario). Команды возвращают nil, если запись не говорит иначе. Ошибка аргументов/ссылок/квот отклоняет весь вычисленный список; прежние успешно исполненные блоки не откатываются. После принятия команды очередь исполняется по порядку; последующий runtime-сбой не является транзакционным откатом всей истории.

Строковые/числовые аргументы старого `game` читаются MoonSharp `AsType`: числа могут преобразовываться в строки и числовые строки — в числа. Boolean/nil не являются такими аргументами. Числа после чтения обязаны быть конечными. Большинство вызовов **не проверяет лишние аргументы** и игнорирует их; для `wait_click`/`start_minigame` число аргументов определяет вариант. Все обязательные позиции без значения или с nil — ошибка, кроме явно указанных Value-позиций. Функции `get`, `time`, `visible`, `object`, `minigame_result` читают снимок, а не симулируют ещё не исполненную очередь. Только `set` обновляет локальную копию строковых переменных.

Для примеров с `refs` заранее объявите в свойствах блока/ресурса соответствующие объекты: hero — персонаж, happy — его состояние, line — фраза, scene — сцена, script/after_puzzle — Lua-ресурсы, puzzle — сцена мини-игры. item — непустой GUID области, action — существующая низкоуровневая команда (не GUID фразы).

<a id="game-get"></a>
## game.get

Прочитать строковую переменную сюжета.

```lua
game.get(key) -> string | nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** key: строка; чтение само не проверяет формат/длину имени. Обязателен, nil недопустим.

**Возврат:** Строка, включая пустую, либо nil, если ключа нет. Видит предшествующий game.set в этом вычислении.

**Эффект и ограничения:** Немедленное чтение локального снимка переменных; команды не создаёт. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local flag = game.get("door.open")
if flag == "true" then game.wait_input() end
```

**Связи:** [game.set](scenario.md#game-set), [ui.get](state-and-random.md#ui-get). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-set"></a>
## game.set

Задать строковую переменную сюжета.

```lua
game.set(key, value) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** key: непустая и не состоящая из пробелов строка до 128 единиц UTF-16; value: строка до 8192. Оба обязательны; nil не удаляет ключ и даёт ошибку. Числа принимаются строковым преобразованием MoonSharp, но предпочтительны явные строки.

**Возврат:** nil. Последующий game.get в этом блоке сразу видит новое значение.

**Эффект и ограничения:** Сначала меняет рабочую копию VM, затем создаёт SetAction. Host изменится при исполнении очереди. До 256 переменных и 131072 символов ключей+значений; переполнение отвергает весь результат Lua. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.set("door.open", "true")
```

**Связи:** [game.get](scenario.md#game-get), [ui.set_persistent](session-and-storage.md#ui-set-persistent). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-time"></a>
## game.time

Прочитать игровое время сценария в секундах.

```lua
game.time() -> number
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Конечное число из снимка на начало вызова.

**Эффект и ограничения:** Немедленное чтение; game.wait в этом же блоке не изменяет возвращаемое время. В паузе и при pending Lua время сюжета не идёт. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
if game.time() > 30 then game.set("late", "true") end
```

**Связи:** [game.wait](scenario.md#game-wait). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-visible"></a>
## game.visible

Узнать, показан ли персонаж.

```lua
game.visible(objectId) -> boolean
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: строка с непустым GUID. Существование объекта для чтения не проверяется.

**Возврат:** true, если GUID есть в снимке VisibleObjects; иначе false.

**Эффект и ограничения:** Немедленное чтение исходного снимка; не видит game.show/hide этого блока. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
if not game.visible(refs.hero) then game.show(refs.hero) end
```

**Связи:** [game.show](scenario.md#game-show), [game.hide](scenario.md#game-hide), [game.object](scenario.md#game-object). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-object"></a>
## game.object

Прочитать показ, состояние и ручное размещение персонажа.

```lua
game.object(objectId) -> table
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: строка GUID; неизвестный непустой GUID допускается для чтения.

**Возврат:** {visible:boolean, state?:string, x?:number, y?:number, scale?:number, layer?:integer}. state — GUID состояния; x/y/scale/layer есть только при ручном размещении; автоматические координаты не вычисляются.

**Эффект и ограничения:** Отдельная таблица из исходного снимка; её изменение не меняет игру. Не видит подготовленные команды. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local p = game.object(refs.hero)
if p.visible then game.hide(refs.hero) end
```

**Связи:** [game.visible](scenario.md#game-visible), [game.place](scenario.md#game-place), [game.state](scenario.md#game-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-show"></a>
## game.show

Показать персонажа с запомненным либо начальным состоянием.

```lua
game.show(objectId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: GUID существующего персонажа, обычно refs.hero.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди. Повторный показ не дублирует персонажа. При отсутствующем изображении host добавляет диагностику и не показывает персонажа. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.show(refs.hero)
```

**Связи:** [game.hide](scenario.md#game-hide), [game.state](scenario.md#game-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-hide"></a>
## game.hide

Скрыть персонажа.

```lua
game.hide(objectId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: GUID существующего персонажа.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди. Уже скрытый персонаж — no-op; состояние и размещение остаются. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.hide(refs.hero)
```

**Связи:** [game.show](scenario.md#game-show), [game.visible](scenario.md#game-visible). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-state"></a>
## game.state

Выбрать состояние персонажа.

```lua
game.state(objectId, stateId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** Оба аргумента — GUID; stateId должен принадлежать именно objectId.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди. У скрытого персонажа запоминает состояние, но не показывает его. Чужое состояние отвергается при проверке всего пакета. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.state(refs.hero, refs.happy)
game.show(refs.hero)
```

**Связи:** [game.show](scenario.md#game-show), [game.object](scenario.md#game-object). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-text"></a>
## game.text

Показать текст существующей фразы текущей сцены.

```lua
game.text(lineId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** lineId: GUID фразы текущей сцены; не произвольный текст.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди: меняет текущую фразу и запускает её effect/voice. Не исполняет LineEntry, размещение, блоки «До» и не добавляет ожидание ввода; обычно порядок фраз уже делает это автоматически. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.text(refs.line)
game.wait_input()
```

**Связи:** [game.jump_line](scenario.md#game-jump-line), [game.wait_input](scenario.md#game-wait-input). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-place"></a>
## game.place

Задать ручное размещение персонажа.

```lua
game.place(objectId, x, y, scale, layer) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: GUID персонажа. x,y: конечные числа [-1,2], доли сцены, точка снизу по центру спрайта. scale: [0.05,5], относительно вписывания 360×560. layer: целое [-1000,1000], больший рисуется выше. Все аргументы обязательны, nil запрещён.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди. Прерывает прежнее движение этого персонажа; не показывает скрытого персонажа. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.place(refs.hero, 0.5, 0.95, 1, 0)
```

**Связи:** [game.auto](scenario.md#game-auto), [game.move](scenario.md#game-move). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/PlacementTests.cs).

<a id="game-auto"></a>
## game.auto

Вернуть автоматическое размещение персонажа.

```lua
game.auto(objectId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: GUID существующего персонажа.

**Возврат:** nil.

**Эффект и ограничения:** Удаляет ручное размещение и активное движение этого персонажа при исполнении команды. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.auto(refs.hero)
```

**Связи:** [game.place](scenario.md#game-place), [game.object](scenario.md#game-object). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-move"></a>
## game.move

Запустить движение персонажа к размещению.

```lua
game.move(objectId, x, y, scale, layer, seconds) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** objectId: GUID персонажа. x,y: конечные числа [-1,2], доли сцены, точка снизу по центру спрайта. scale: [0.05,5], относительно вписывания 360×560. layer: целое [-1000,1000], больший рисуется выше. Все аргументы обязательны, nil запрещён. seconds: конечное число [0,86400] секунд; 0 устанавливает цель сразу.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди. Новое движение заменяет прежнее этого персонажа; интерполяция выполняется host параллельно дальнейшим командам. Слой цели используется во время движения. Для барьера добавьте wait_actions. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.move(refs.hero, 0.7, 0.95, 1, 1, 2)
game.wait_actions()
```

**Связи:** [game.wait_actions](scenario.md#game-wait-actions), [game.place](scenario.md#game-place). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/PlacementTests.cs).

<a id="game-wait"></a>
## game.wait

Задержать сценарную очередь на игровое время.

```lua
game.wait(seconds) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** seconds: обязательное конечное число [0,86400]; nil запрещён.

**Возврат:** nil; отложенного Lua-возврата нет.

**Эффект и ограничения:** Команда очереди, а не yield Lua. 0 — no-op. Пауза удерживает таймер, движения и сюжетное время. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.wait(1)
game.set("after.timer", "true")
```

**Связи:** [game.time](scenario.md#game-time), [game.wait_input](scenario.md#game-wait-input). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-wait-input"></a>
## game.wait_input

Ожидать продолжения игроком.

```lua
game.wait_input() -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** nil.

**Эффект и ограничения:** Ставит input-барьер при исполнении очереди. Только событие продолжения снимает его; завершение движения или таймера не заменяет ввод. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.wait_input()
```

**Связи:** [game.wait](scenario.md#game-wait), [game.text](scenario.md#game-text). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-wait-actions"></a>
## game.wait_actions

Ожидать окончания движений сценария.

```lua
game.wait_actions() -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** nil.

**Эффект и ограничения:** Барьер очереди по ActiveActions. При отсутствии движений — no-op. Не ждёт анимации ui.animate, звук или сервисы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.move(refs.hero, 0.5, 0.95, 1, 0, 1)
game.wait_actions()
```

**Связи:** [game.move](scenario.md#game-move), [ui.animate](animations.md#ui-animate). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-wait-click"></a>
## game.wait_click

Ожидать щелчка по сценарной активной области.

```lua
game.wait_click() -> nil
game.wait_click(token) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** Без аргументов — любой включённый hotspot. token: GUID нужной области. Явный nil отличается от отсутствия: это ошибка GUID.

**Возврат:** nil. Следующий Lua-блок читает game.get("input.object") — GUID выбранной области.

**Эффект и ограничения:** Барьер очереди. Если при его исполнении нет подходящего активного hotspot, сценарий завершается ошибкой. Это не событие click обработчика компонентов. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.hotspot(refs.item, "Ключ", 0.1, 0.2, 0.1, 0.1)
game.wait_click(refs.item)
```

**Связи:** [game.hotspot](scenario.md#game-hotspot), [game.get](scenario.md#game-get). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-hotspot"></a>
## game.hotspot

Создать сценарную прямоугольную активную область.

```lua
game.hotspot(token, label, x, y, width, height) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** token: любой непустой GUID, не обязательно ID персонажа; label: строка до 8192, пустая допустима. x,y >=0; width,height >0; x+width<=1, y+height<=1. Координаты — доли сцены. Все обязательны.

**Возврат:** nil.

**Эффект и ограничения:** Команда очереди. Область с тем же token заменяется, не добавляется повторно. API удаления hotspot нет; переход очищает области. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.hotspot("11111111-1111-1111-1111-111111111111", "Осмотреть", 0, 0, 0.2, 0.2)
```

**Связи:** [game.wait_click](scenario.md#game-wait-click). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-finish"></a>
## game.finish

Записать старое строковое поле результата.

```lua
game.finish(result) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** result: обязательная строка до 8192. nil запрещён.

**Возврат:** nil.

**Эффект и ограничения:** Создаёт SetAction для minigame.result. Не завершает активную мини-игру и не эквивалентна ui.complete_minigame. Локальная копия game.get этим вызовом не обновляется: это особенность реализации. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.finish("win")
```

**Связи:** [game.get](scenario.md#game-get), [game.minigame_result](scenario.md#game-minigame-result), [ui.complete_minigame](minigames.md#ui-complete-minigame). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-run"></a>
## game.run

Поставить выполнение Lua-ресурса в очередь сценария.

```lua
game.run(scriptId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** scriptId: GUID зарегистрированного Lua-ресурса, обычно refs.script; не путь.

**Возврат:** nil; ресурс не возвращает Lua-значение вызывающему коду.

**Эффект и ограничения:** Новая VM запускается, когда очередь доходит до команды. Использует свои refs и актуальный на этот момент снимок. Команды ресурса вставляются перед оставшейся очередью. Это не ui.import и не общий стек/локалы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.run(refs.script)
```

**Связи:** [ui.import](modules.md#ui-import), [game.get](scenario.md#game-get). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScriptAssetTests.cs).

<a id="game-jump"></a>
## game.jump

Перейти к низкоуровневой команде сцены.

```lua
game.jump(sceneId, actionId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** sceneId/actionId: существующая пара GUID сцены и скомпилированной команды. GUID фразы не является actionId.

**Возврат:** nil.

**Эффект и ограничения:** При исполнении меняет позицию, очищает остаток очереди и hotspots. Время, переменные, состояния/размещения/показ персонажей и движения сохраняются. При другой сцене LineId сбрасывается, в той же сцене остаётся до следующего text. Привязки фразы исполняются только при входе в её LineEntry. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.jump(refs.scene, refs.action)
```

**Связи:** [game.jump_line](scenario.md#game-jump-line), [game.choice](scenario.md#game-choice). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-jump-line"></a>
## game.jump_line

Перейти к началу авторской фразы.

```lua
game.jump_line(sceneId, lineId) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** sceneId/lineId: GUID сцены и принадлежащей ей фразы.

**Возврат:** nil.

**Эффект и ограничения:** Как jump, но вычисляет стабильный LineEntryId. При входе исполняет персонажей, размещение и блоки «До фразы», затем текст и дальнейшие фазы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.jump_line(refs.scene, refs.line)
```

**Связи:** [game.jump](scenario.md#game-jump), [game.text](scenario.md#game-text). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/PhraseScriptTests.cs).

<a id="game-choice"></a>
## game.choice

Остановить сценарий на выборе игрока.

```lua
game.choice(options) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** options: таблица с последовательностью 1..N, N=1..8. Каждый элемент: {id:string GUID,text:string,scene:string GUID,action:string GUID}. id уникальны и непусты; text не пуст/не пробельный и <=8192; scene/action — существующая цель. Поля обязательны, nil запрещён. Проверяется #table и эта последовательность; дополнительные поля таблицы не являются вариантами.

**Возврат:** nil. Решение применяется host как переход, а не возвращается в текущую VM.

**Эффект и ограничения:** Команда очереди. Использует низкоуровневые action ID, прямого варианта choice с lineId нет. Не выдумывайте ui.choice или game.line_entry_id. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.choice({{id="22222222-2222-2222-2222-222222222222", text="Войти", scene=refs.scene, action=refs.action}})
```

**Связи:** [game.jump](scenario.md#game-jump), [game.jump_line](scenario.md#game-jump-line). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs).

<a id="game-start-minigame"></a>
## game.start_minigame

Запустить авторскую сцену как мини-игру с барьером сюжета.

```lua
game.start_minigame(sceneId) -> nil
game.start_minigame(sceneId, parameters) -> nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** sceneId: GUID сцены с включённой MinigameDefinition и обработчиком. Без второго аргумента берутся DefaultParameters ресурса. Явный nil означает данные null и должен соответствовать схеме. parameters: сериализуемое значение, см. данные; ui.list/ui.object/ui.null в game-VM отсутствуют.

**Возврат:** nil. Структурированный итог доступен следующим выполняемым блоком через game.minigame_result().

**Эффект и ограничения:** Команда очереди; проверяются ресурс, параметры и схема. Когда команда исполнена, сюжет ждёт completed/cancelled/failed. Состояние начинается с InitialState, RNG — начальный. Нельзя получить результат следующим выражением в этом же вычислении. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
game.start_minigame(refs.puzzle, {target=3})
game.run(refs.after_puzzle)
```

**Связи:** [game.minigame_result](scenario.md#game-minigame-result), [ui.complete_minigame](minigames.md#ui-complete-minigame). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/MinigameTests.cs).

Если параметрам нужны пустой список или вложенный null, задайте их в DefaultParameters ресурса через JSON и вызовите функцию без второго аргумента. В сценарной VM нет ui.list/ui.null: `{}` передаётся как Object, а nil внутри Lua-таблицы удаляет поле. Не переносите примеры сериализации из ui-контекста в game без этой поправки.

<a id="game-minigame-result"></a>
## game.minigame_result

Прочитать последний структурированный итог мини-игры.

```lua
game.minigame_result() -> table | nil
```

**Доступность:** Сценарный блок/сценарный ресурс; не ui и не импорт-модуль. См. [общие правила страницы](scenario.md), [lifetime](execution-model.md#lifetime).

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** {status="completed"|"cancelled"|"failed",data=Value} либо nil. data — результат схемы при completed; при отмене/ошибке — непрозрачный null-маркер, не nil. Message и sceneId в Lua-проекции отсутствуют.

**Эффект и ограничения:** Немедленный исходный снимок. Новый start_minigame очищает прошлый итог при исполнении. В game-VM нет ui.null для сравнения: проверяйте status до обращения к data. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local r = game.minigame_result()
if r and r.status == "completed" and r.data.score >= 3 then
  game.set("puzzle.won", "true")
end
```

**Связи:** [game.start_minigame](scenario.md#game-start-minigame), [game.finish](scenario.md#game-finish). [Регистрация и аргументы](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).
