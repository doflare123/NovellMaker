# Мини-игры и возврат результата

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Мини-игра — обычная авторская сцена с холстом, обработчиком и включённым ресурсом мини-игры. Она запускается только сценарием `game.start_minigame(sceneId[,parameters])`; четыре функции ui этой страницы доступны **только активному обработчику этого экземпляра мини-игры**, включая вызываемый модуль. В обычном ui-экране они существуют, но дают ошибку контекста. `game.finish` всего лишь записывает старую строковую переменную и lifecycle не завершает.

Настройте ParametersSchema, StateSchema, ResultSchema, DefaultParameters, InitialState, CanSave и UpdatesPerSecond в свойствах сцены. Kind: Null/Boolean/Number/String/List/Object. У Object Fields при непустом словаре задаёт точный набор обязательных полей и их непосредственных типов; до 64 полей, ключи непустые/непробельные <=128. Пустой Fields не ограничивает набор полей; вложенные подробные JSON schemas не поддерживаются. GUI редактирует поля объектных схем и сохраняет уже заданный тип корня; остальные типы схем доступны в формате проекта. Все значения также проходят [Value-квоты](state-and-random.md). Частота update — [1,20] Гц, по умолчанию **5**. Одновременно активна одна мини-игра сюжетного сеанса.

Общие правила каждой функции: [атомарность](execution-model.md#batch), [restore](execution-model.md#restore), [сроки жизни](execution-model.md#lifetime), [квоты](limits.md#values). Новая игра получает InitialState и фиксированный начальный RNG; параметры отделены от state. Прикрытие любым верхним слоем или неактивность host ставит мини-игру на паузу; результаты held до снятия. После completed/cancelled/failed сюжет продолжает очередь и читает итог **новой VM**. Ошибка обработчика даёт failed с data=null, закрытие экрана — cancelled; Message виден host, но game.minigame_result его не проецирует.

### Сохранение и восстановление

CanSave по умолчанию true. При false используется авторская SaveDisabledReason (непустая строка <=256). Сохранение запрещено до InitialState/on_load commit, при pending обработчика, удержании указателя/клавиш, живой анимации/звуке и их результатах, проверке выпуска и её результате. При допустимой точке слот хранит параметры, state, RNG, ID сцены/действия/запуска и признак Initialized; не дерево, Lua-стек, locals, closures, input, таймлайны или источники звука.

Восстанавливающий on_load строит вид из ui.get_state и ui.minigame_parameters. Можно менять текст/геометрию/показ/иерархию, логировать, импортировать модули. Нельзя менять подтверждённые state/RNG, делать сервисы/триггеры/ввод, запускать/отменять анимации/звук, close или завершать мини-игру. Повторный set_state того же канонического значения допускается, но проще его не делать. Флаг is_restoring остаётся true у всего восстановленного экземпляра: проверяйте его **в on_load**, а не отключайте по нему все события.

В примерах result/state schema Object с полем score:Number; для Parameters target:Number, InitialState={score=0}, DefaultParameters={target=3}. Полный восстанавливаемый пример — [примеры](examples.md#minigame).

<a id="ui-minigame-parameters"></a>
## ui.minigame_parameters

Прочитать параметры текущего запуска мини-игры.

```lua
ui.minigame_parameters() -> Value
```

**Доступность:** Только ui on_load/on_event мини-игры и вызванный модуль; restore — по исключению ниже. В сценарии и обычном ui-контексте не вызывается.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Отделённая копия Parameters; null представлен ui.null.

**Эффект и ограничения:** Только в обработчике мини-игры; в обычном экране ошибка. В restore читает сохранённые параметры. Изменение таблицы не меняет параметры. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local p = ui.minigame_parameters()
```

**Связи:** [game.start_minigame](scenario.md#game-start-minigame). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).

<a id="ui-is-restoring"></a>
## ui.is_restoring

Узнать, восстанавливается ли мини-игра из слота.

```lua
ui.is_restoring() -> boolean
```

**Доступность:** Только ui on_load/on_event мини-игры и вызванный модуль; restore — по исключению ниже. В сценарии и обычном ui-контексте не вызывается.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** boolean, флаг всего данного экземпляра, а не только первого on_load.

**Эффект и ограничения:** Только в обработчике мини-игры; в обычном экране ошибка. Не используйте флаг для отключения обычных on_event восстановленной игры. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local restored = ui.is_restoring()
```

**Связи:** [ui.get_state](state-and-random.md#ui-get-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).

<a id="ui-complete-minigame"></a>
## ui.complete_minigame

Завершить мини-игру структурированным результатом.

```lua
ui.complete_minigame(result) -> nil
ui.complete_minigame() -> nil
```

**Доступность:** Только ui on_load/on_event мини-игры и вызванный модуль; restore — по исключению ниже. В сценарии и обычном ui-контексте не вызывается.

**Аргументы и ошибки:** result: Value, проверяемое ResultSchema. Отсутствие/nil — null, допустимо лишь для схемы Null.

**Возврат:** nil; следующий сценарный Lua читает completed и data.

**Эффект и ограничения:** Последняя команда пакета, только активная мини-игра. Несовместима с close/сервисом/триггером. После commit очищает владельца и снимает барьер сюжета. Restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.complete_minigame({score=3})
```

**Связи:** [game.minigame_result](scenario.md#game-minigame-result). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).

<a id="ui-cancel-minigame"></a>
## ui.cancel_minigame

Завершить мини-игру без результата автора.

```lua
ui.cancel_minigame() -> nil
```

**Доступность:** Только ui on_load/on_event мини-игры и вызванный модуль; restore — по исключению ниже. В сценарии и обычном ui-контексте не вызывается.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** nil; сценарный итог status=cancelled,data=null.

**Эффект и ограничения:** Те же правила последней команды и несовместимости, что у complete_minigame. Message host: «Отменено автором.». Не исключение Lua. Restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.cancel_minigame()
```

**Связи:** [ui.complete_minigame](minigames.md#ui-complete-minigame). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).
