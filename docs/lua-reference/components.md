# Компоненты, геометрия и иерархия

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Контекст: обработчик компонентов корневой сцены, слоя или мини-игры; функции импортированного модуля могут вызываться этим обработчиком. `game.*` здесь нет. Все изменения входят в [атомарный пакет](execution-model.md#batch); [восстанавливающий on_load](execution-model.md#restore) разрешает строить вид. Исключение по применимости к lifecycle — `ui.close`.

Общие правила каждой функции: [handles и данные](execution-model.md#namespaces), [lifetime](execution-model.md#lifetime), [квоты дерева](limits.md#components), [события](events.md). Сохранение обычного экрана в слот не выполняется; у мини-игры сохраняются state/RNG, а дерево заново строится из них. Worker обновляет рабочее дерево последовательно, поэтому новый handle можно использовать в следующих командах того же пакета. Изменения видны игроку только после проверки host.

Обычные Text-компоненты runtime переносят длинные строки по ширине, сохраняют явные LF и выводят многострочный текст сверху. Большие Text в пределах допустимой стороны выводятся полосами с общими лимитами текстур/кадра/кэша. Высота компонента ограничивает видимую область; автоматического скролла нет. Кнопки сохраняют однострочную подпись. Для длинной истории выбирайте размер страницы под авторский макет: [шаблоны чтения](history.md).

По умолчанию старые ui-bindings этой страницы не требуют точного числа аргументов; лишние игнорируются. Обязательный handle/text читается строковым `AsType`, но практически используйте точные строки handles. В старых set_visible/set_enabled MoonSharp AsType(Boolean) также допускает преобразование по истинности Lua: например, 0 и пустая строка дают true; строгий режим требует настоящий boolean. Для точного числа аргументов и типов включите [ui.strict_arguments(true)](#ui-strict-arguments) до возврата handlers. Указания об игнорировании лишних аргументов ниже относятся к режиму по умолчанию. Ошибка чужого/удалённого handle, дерева, геометрии или общего бюджета отклоняет пакет и останавливает ошибочный обработчик. Рендер и остальные независимые экземпляры могут продолжаться; поломка общей беседы затрагивает все её VM.

### Геометрия

Все числовые значения конечны. Логический viewport **1280×720**; x/y ребёнка локальны родителю, а e.x/e.y — координаты viewport. Итоговая матрица учитывает anchor, pivot, scale и rotation.

| Поле patch | Диапазон и смысл |
| --- | --- |
| x, y | [-100000,100000], логические пиксели относительно anchor родителя |
| width, height | (0,100000], логические пиксели |
| anchor_x, anchor_y | [0,1], доли размера родителя |
| pivot_x, pivot_y | [0,1], доли размера самого узла |
| scale_x, scale_y | модуль [0.001,1000]; отрицательный отражает; 0 запрещён |
| rotation | [-360000,360000], градусы |
| opacity | [0,1], умножается на opacity предков |
| color | точная строка #RRGGBB; умножается на tint предков |
| order | целое [-100000,100000], порядок среди siblings, больший выше |
| clip_children | boolean, ограничивает потомков полигоном родителя |

После преобразования координаты мировых углов должны оставаться в [-1e8,1e8]; матрица обратима с |determinant|>=1e-12. Проверяется всё дерево, не только локальное поле. Изображение, имя, тип и привязку диалога через Lua менять нельзя. `ui.create` копирует существующий шаблон; универсального конструктора узла по таблице нет.

Во всех примерах refs.label — Text, refs.button — Button/Area, refs.panel — Panel/Container, refs.template — некорневое поддерево этого же холста; label для примера create должен быть внутри template.

<a id="ui-find"></a>
## ui.find

Получить узел по авторскому имени ссылки.

```lua
ui.find(alias) -> handle | nil
```

**Доступность:** ui on_load/on_event, модуль, мини-игра и вычисление исходника; в restore допустима. До события snapshot ещё пустой.

**Аргументы и ошибки:** alias: обязательная строка имени refs текущего холста. Не имя компонента и не GUID.

**Возврат:** handle текущего живого узла либо nil для неизвестного/удалённого.

**Эффект и ограничения:** Немедленно смотрит рабочее дерево VM; после remove уже возвращает nil. Не ищет автоматически созданные копии. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local label = ui.find("label")
if label then ui.set_text(label, "Привет") end
```

**Связи:** [ui.create](components.md#ui-create), [ui.remove](components.md#ui-remove). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-set-text"></a>
## ui.set_text

Заменить текст компонента.

```lua
ui.set_text(node, text) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** node: собственный живой handle. text: строка <=8192 UTF-16; nil запрещён. Для старого строкового чтения MoonSharp допускает число как строку.

**Возврат:** nil.

**Эффект и ограничения:** Изменение пакета; суммарный текст всех set_text <=131072, столько же текст всего дерева. Тип узла не ограничен Text/Button, но видимость текста зависит от renderer. Привязки Dialogue Text/Speaker производятся host и могут заменить авторский текст. Геометрически допустимый текст может не попасть в бюджет растра: отдельная диагностика host, кнопка остаётся без подписи, пакет не откатывается. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_text(refs.label, "Готово")
```

**Связи:** [ui.find](components.md#ui-find), [ui.set_state](state-and-random.md#ui-set-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-set-visible"></a>
## ui.set_visible

Изменить показ компонента.

```lua
ui.set_visible(node, value) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** node: собственный живой handle; value: boolean. По умолчанию сохранено AsType(Boolean): числа (включая 0), строки и таблицы дают true, false даёт false. В строгом режиме требуется ровно boolean, nil/числа запрещены.

**Возврат:** nil.

**Эффект и ограничения:** Изменение пакета. Флаги наследуются потомками. Потеря доступности активной кнопки/области сбрасывает capture/focus/hover с соответствующими событиями. enabled=false не скрывает отрисовку. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_visible(refs.button, false)
```

**Связи:** [ui.set_visible](components.md#ui-set-visible), [ui.set_enabled](components.md#ui-set-enabled), [ui.focus](input-and-triggers.md#ui-focus). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-set-enabled"></a>
## ui.set_enabled

Изменить доступность ввода компонента.

```lua
ui.set_enabled(node, value) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** node: собственный живой handle; value: boolean. По умолчанию сохранено AsType(Boolean): числа (включая 0), строки и таблицы дают true, false даёт false. В строгом режиме требуется ровно boolean, nil/числа запрещены.

**Возврат:** nil.

**Эффект и ограничения:** Изменение пакета. Флаги наследуются потомками. Потеря доступности активной кнопки/области сбрасывает capture/focus/hover с соответствующими событиями. enabled=false не скрывает отрисовку. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_enabled(refs.button, false)
```

**Связи:** [ui.set_visible](components.md#ui-set-visible), [ui.set_enabled](components.md#ui-set-enabled), [ui.focus](input-and-triggers.md#ui-focus). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-set-geometry"></a>
## ui.set_geometry

Изменить перечисленные свойства геометрии узла.

```lua
ui.set_geometry(node, patch) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** node: собственный живой handle. patch: таблица только из полей таблицы «Геометрия» ниже; пропущенное поле или nil сохраняет прежнее значение. Неизвестный ключ — ошибка. Значения не приводятся из строк.

**Возврат:** nil.

**Эффект и ограничения:** Весь новый геометрический снимок проверяется до commit, включая мировые координаты/матрицы. Прямая запись анимируемого свойства отменяет затрагивающую его группу с reason=property_set. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_geometry(refs.label, {x=100, opacity=0.5, rotation=10})
```

**Связи:** [ui.tween](animations.md#ui-tween), [ui.set_parent](components.md#ui-set-parent). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/VisualScreenTests.cs).

<a id="ui-set-parent"></a>
## ui.set_parent

Переместить поддерево под другого родителя.

```lua
ui.set_parent(node, parent) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Оба handle принадлежат этому экземпляру. parent: Container или Panel. Корень перемещать нельзя; циклы, чужой/удалённый родитель и превышение глубины — ошибка.

**Возврат:** nil.

**Эффект и ограничения:** Изменение пакета. Локальные координаты остаются прежними, мировое положение может измениться. Отменяет все живые анимационные группы экземпляра с reason=reparented. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_parent(refs.label, refs.panel)
```

**Связи:** [ui.create](components.md#ui-create), [ui.remove](components.md#ui-remove). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenTreeTests.cs).

<a id="ui-remove"></a>
## ui.remove

Удалить узел и всех его потомков.

```lua
ui.remove(node) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** node: собственный живой некорневой handle; повторное удаление — ошибка.

**Возврат:** nil.

**Эффект и ограничения:** Изменение пакета. Удалённые ID уходят в retired и не переиспользуются; их анимации отменяются (removed), ввод сбрасывается. refs по-прежнему содержит старую строку; ui.find вернёт nil. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.remove(refs.template)
```

**Связи:** [ui.find](components.md#ui-find), [ui.create](components.md#ui-create). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenTreeTests.cs).

<a id="ui-create"></a>
## ui.create

Копировать существующее поддерево.

```lua
ui.create(template, parent) -> handle, table<handle,handle>
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** template: собственный живой узел; parent: собственный Container/Panel. Оба обязательны. Ограничения дерева/identity проверяются для полной копии.

**Возврат:** Два немедленных результата: handle корня копии и словарь oldHandle→newHandle для всех узлов. Новые строки пригодны для следующих команд этого пакета.

**Эффект и ограничения:** Подготавливает copy в пакете; копирует данные узлов, но не VM/состояние/анимацию/владельца звука. Новых refs и обработчиков на каждый узел не создаёт. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local copy, map = ui.create(refs.template, refs.panel)
ui.set_visible(copy, true)
ui.set_text(map[refs.label], "Копия")
```

**Связи:** [ui.remove](components.md#ui-remove), [ui.set_parent](components.md#ui-set-parent). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenTreeTests.cs).

<a id="ui-close"></a>
## ui.close

Закрыть собственный экземпляр холста.

```lua
ui.close() -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Нет аргументов; по умолчанию лишние игнорируются, в строгом режиме запрещены.

**Возврат:** nil.

**Эффект и ограничения:** Последняя команда пакета. Несовместима с сервисом, триггером и итогом мини-игры; команда после close отвергает весь пакет. Очищает VM, state/RNG, ввод и собственные звук/анимации/запросы; закрытие мини-игры даёт cancelled. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_text(refs.label, "Закрываем")
ui.close()
```

**Связи:** [ui.cancel_minigame](minigames.md#ui-cancel-minigame), [ui.main_menu](session-and-storage.md#ui-main-menu). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-strict-arguments"></a>
## ui.strict_arguments

Включить или выключить строгую проверку аргументов текущего экземпляра холста.

```lua
ui.strict_arguments(enabled) -> nil
```

**Доступность:** ui при вычислении исходника, on_load/on_event, вызываемые модули и мини-игры; при restore также допустима.

**Аргументы и ошибки:** Ровно один boolean, даже в режиме по умолчанию. nil, строка/число, отсутствие или лишний аргумент — ошибка.

**Возврат:** nil.

**Эффект и ограничения:** По умолчанию false. true требует показанное число аргументов и точные типы старых ui-bindings без преобразования чисел в строки и значений в boolean. ui.import также требует ровно один ID. Общая политика распространяется на этот экземпляр и все его модули, сохраняется между событиями; другие экземпляры независимы. Включение в начале корневого исходника применяется и к последующим импортам. Обычные правила контекста и квот действуют в обоих режимах. Необязательные аргументы и явно разрешённый nil сохраняются: save_slot(), save_slot(nil,nil), set_state(nil), complete_minigame(nil) допустимы; set_state()/complete_minigame() без аргумента в строгом режиме запрещены. false восстанавливает прежнюю проверку старых функций; audio/RNG/animation/standard, log и команды ввода сохраняют собственные строгие сигнатуры в обоих режимах. Переключатель не создаёт команды и не сериализуется. При повторном открытии/Load исходник вычисляется заново: включайте режим до возврата handlers, чтобы он применялся после восстановления. Квоты и события прежние.

**Пример**:

```lua
ui.strict_arguments(true)
return {on_load=function() ui.set_text(refs.label, "Готово") end,
        on_event=function(e) end}
```

**Связи:** [game.strict_arguments](scenario.md#game-strict-arguments), [модули](modules.md), [квоты](limits.md#components). [Регистрация](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [общий валидатор](../../src/NovellMaker.Core/Scripting/LuaApiArguments.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaApiContractTests.cs).
