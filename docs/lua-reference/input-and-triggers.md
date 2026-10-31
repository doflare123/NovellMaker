# Ввод и авторские триггеры

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Контекст: on_load/on_event обработчика холста/слоя/мини-игры, включая функции вызываемых модулей. Сценарного game здесь нет. [Схемы событий](events.md), [правила пакета/restore](execution-model.md#batch), [lifetime](execution-model.md#lifetime), [квоты](limits.md#events) обязательны для всех записей. Восстанавливающий on_load мини-игры не может выдавать ни команды ввода, ни триггеры.

Для физического ввода верните `input_events=true`. Без флага остаётся совместимый обработчик `click` от назначения Lua кнопки; расширенные события не поступают. Координаты событий — логические пиксели viewport, не локальные координаты узла. Key-события — физические клавиши, не ввод Unicode-текста. Capture/focus не сохраняются в слот, сбрасываются на закрытии, модальном переключении, потере фокуса и смене сеанса. Кнопка примера должна иметь действие Lua, иначе её штатная команда обрабатывается host вместо click.

Триггер адресован только отправителю, не является глобальным broadcast или изменением переменной. Он исчезает при закрытии/Reset/Load и не восстанавливается из слота. Для хранения факта используйте state/профиль. `refs.button` — доступный Button/Area собственного холста.

<a id="ui-capture-pointer"></a>
## ui.capture_pointer

Перенаправить текущую последовательность указателя своему узлу.

```lua
ui.capture_pointer(node) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно один собственный доступный видимый Button/Area handle. Требуется input_events=true в таблице обработчиков. nil — не handle.

**Возврат:** nil.

**Эффект и ограничения:** Команда пакета. Capture/release действуют только на ещё живой исходный press этого экземпляра; поздний ответ после отпускания/смены эпохи — no-op. focus работает по текущей эпохе. release создаёт pointer_cancel, а не click; focus создаёт blur/focus. Restore on_load мини-игры запрещает команды ввода. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.capture_pointer(refs.button)
```

**Связи:** [ui.focus](input-and-triggers.md#ui-focus), [ui.capture_pointer](input-and-triggers.md#ui-capture-pointer), [ui.release_pointer](input-and-triggers.md#ui-release-pointer). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaSessionTests.cs).

<a id="ui-release-pointer"></a>
## ui.release_pointer

Отменить собственную текущую последовательность указателя.

```lua
ui.release_pointer() -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно ноль аргументов. Требуется input_events=true в таблице обработчиков. nil — не handle.

**Возврат:** nil.

**Эффект и ограничения:** Команда пакета. Capture/release действуют только на ещё живой исходный press этого экземпляра; поздний ответ после отпускания/смены эпохи — no-op. focus работает по текущей эпохе. release создаёт pointer_cancel, а не click; focus создаёт blur/focus. Restore on_load мини-игры запрещает команды ввода. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.release_pointer()
```

**Связи:** [ui.focus](input-and-triggers.md#ui-focus), [ui.capture_pointer](input-and-triggers.md#ui-capture-pointer), [ui.release_pointer](input-and-triggers.md#ui-release-pointer). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaSessionTests.cs).

<a id="ui-focus"></a>
## ui.focus

Передать клавиатурный фокус своему узлу.

```lua
ui.focus(node) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно один собственный доступный видимый Button/Area handle. Требуется input_events=true в таблице обработчиков. nil — не handle.

**Возврат:** nil.

**Эффект и ограничения:** Команда пакета. Capture/release действуют только на ещё живой исходный press этого экземпляра; поздний ответ после отпускания/смены эпохи — no-op. focus работает по текущей эпохе. release создаёт pointer_cancel, а не click; focus создаёт blur/focus. Restore on_load мини-игры запрещает команды ввода. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.focus(refs.button)
```

**Связи:** [ui.focus](input-and-triggers.md#ui-focus), [ui.capture_pointer](input-and-triggers.md#ui-capture-pointer), [ui.release_pointer](input-and-triggers.md#ui-release-pointer). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaSessionTests.cs).

<a id="ui-trigger"></a>
## ui.trigger

Поставить собственное именованное событие в очередь.

```lua
ui.trigger(id) -> nil
ui.trigger(id, data) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** id: точная строка AuthorKey: [a-z][a-z0-9_.-]{0,63}. data: отсутствует/nil либо точная строка <=1024. Числа не приводятся к строкам.

**Возврат:** nil.

**Эффект и ограничения:** После commit придёт trigger этому же экземпляру, с trigger_id/trigger_data. До 16 за пакет; общий бюджет очереди 128, цепь 32. Нельзя сочетать с сервисом/close/итогом мини-игры. В restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.trigger("door.open", "key")
```

**Связи:** [ui.get_state](state-and-random.md#ui-get-state), [ui.set_persistent](session-and-storage.md#ui-set-persistent). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/PersistentDataLuaTests.cs).
