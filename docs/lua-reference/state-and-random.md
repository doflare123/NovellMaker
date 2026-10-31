# Состояние компонентов и управляемый RNG

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Контекст: `ui`-обработчики и вызываемые ими модули; сценарным `game` эти функции недоступны. Правила каждой записи: [пакет](execution-model.md#batch), [lifetime/слоты](execution-model.md#lifetime), [restore](execution-model.md#restore), [квоты Value и RNG](limits.md#values). `get`/`list`/`object` допускаются также при вычислении исходника; `get_state`, `set_state` и RNG требуют активного on_load/on_event.

RNG — отдельный поток **pcg32-v1** на экземпляр, общий для всех его модулей. Начальное состояние фиксированное, не от системных часов. Независимые экземпляры не расходуют поток друг друга. Состояние RNG и явные данные публикуются только с целым успешным пакетом; локальные переменные/замыкания в слоты не попадают. Сохраняемая мини-игра восстанавливает обе части. Обычный закрытый экран теряет их; повторное открытие начинается заново.

Во фрагментах refs.label — обычный Text собственного холста, не привязанный к диалогу. Пример изменения count предполагает предварительное `ui.set_state({count=0})` в обычном on_load.

### Данные Value

Поддерживаются null, boolean, конечное number (double), строка, плотный список 1..N и словарь строковых ключей. Lua nil в позиции Value означает null. В таблице nil удаляет поле: для сохраняемого null используйте **ui.null**, непрозрачный неизменяемый userdata. Чтение его поля даёт nil, запись (в том числе nil) — ошибку. `type(ui.null)=="userdata"`, `tostring(ui.null)` запрещён. Сравнивайте с `ui.null`.

Обычная пустая таблица — Object; `ui.list({})` — List. Форма помеченных/прочитанных таблиц сохраняется. Смешанные ключи, пропуски списка, циклы, метатаблицы, функции, потоки, прочие userdata, NaN/Infinity и непарные UTF-16 суррогаты отвергаются. Повторная ациклическая ссылка копируется и расходует бюджет повторно. Порядок словаря в каноническом JSON — ordinal.

Каждое значение: 16 уровней контейнеров, 1024 значений с корнем/контейнерами, строка 8192, ключ 128, сумма ключей/строк 32768 UTF-16, канонический JSON 65536 байт. Все set_state одного пакета в сумме также <=65536 байт. Это данные, а не произвольный Lua JSON encoder. Пример с count требует начального `ui.set_state({count=0})`.

<a id="ui-get"></a>
## ui.get

Прочитать переменную сюжета из снимка события.

```lua
ui.get(key) -> string | nil
```

**Доступность:** ui on_load/on_event, модуль, мини-игра и вычисление исходника; в restore допустима. До события snapshot ещё пустой.

**Аргументы и ошибки:** key: обязательная строка, без дополнительной проверки формата при чтении.

**Возврат:** Строка либо nil; не state компонента и не профиль.

**Эффект и ограничения:** Немедленное чтение снимка, захваченного при принятии события. В Menu снимок пустой. Не даёт изменять сюжетные переменные. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_text(refs.label, ui.get("chapter") or "Начало")
```

**Связи:** [game.get](scenario.md#game-get), [ui.get_state](state-and-random.md#ui-get-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-get-state"></a>
## ui.get_state

Прочитать отдельную копию состояния экземпляра.

```lua
ui.get_state() -> Value
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Сериализуемое значение; начальное обычного экрана — ui.null, мини-игры — InitialState. Таблицы каждый раз отделены.

**Эффект и ограничения:** Чтение в обработчике; видит предшествующий set_state того же пакета. Изменение таблицы без set_state ничего не публикует. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = ui.get_state()
if s ~= ui.null then ui.set_text(refs.label, tostring(s.count)) end
```

**Связи:** [ui.set_state](state-and-random.md#ui-set-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-set-state"></a>
## ui.set_state

Подготовить новое явное состояние экземпляра.

```lua
ui.set_state(value) -> nil
ui.set_state() -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** value: Value из таблицы данных ниже. nil и отсутствие аргумента дают null. Сумма канонических JSON всех записей пакета <=65536 байт. У мини-игры также проверяется StateSchema.

**Возврат:** nil.

**Эффект и ограничения:** Рабочее состояние меняется немедленно, host — только при commit полного пакета. Последняя запись побеждает, но бюджет расходуют все. В восстанавливающем on_load мини-игры допустимо только точно то же каноническое значение. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = ui.get_state()
s.count = s.count + 1
ui.set_state(s)
```

**Связи:** [ui.get_state](state-and-random.md#ui-get-state), [ui.list](state-and-random.md#ui-list), [ui.object](state-and-random.md#ui-object). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ComponentStateTests.cs).

<a id="ui-list"></a>
## ui.list

Пометить Lua-таблицу как список сериализуемых данных.

```lua
ui.list(value) -> table
```

**Доступность:** ui on_load/on_event, модуль, мини-игра и вычисление исходника; в restore допустима.

**Аргументы и ошибки:** value: обязательная таблица. Сам вызов только помечает форму; содержимое проверяется при переводе в Value. nil — ошибка.

**Возврат:** Та же таблица, не копия.

**Эффект и ограничения:** Немедленная метка в VM; не команда. Особенно нужна для пустого списка; обычная пустая таблица — словарь. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local data = ui.list({})
```

**Связи:** [ui.set_state](state-and-random.md#ui-set-state), [game.start_minigame](scenario.md#game-start-minigame). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-object"></a>
## ui.object

Пометить Lua-таблицу как словарь сериализуемых данных.

```lua
ui.object(value) -> table
```

**Доступность:** ui on_load/on_event, модуль, мини-игра и вычисление исходника; в restore допустима.

**Аргументы и ошибки:** value: обязательная таблица. Сам вызов только помечает форму; содержимое проверяется при переводе в Value. nil — ошибка.

**Возврат:** Та же таблица, не копия.

**Эффект и ограничения:** Немедленная метка в VM; не команда. Особенно нужна для пустого списка; обычная пустая таблица — словарь. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local data = ui.object({})
```

**Связи:** [ui.set_state](state-and-random.md#ui-set-state), [game.start_minigame](scenario.md#game-start-minigame). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs).

<a id="ui-random"></a>
## ui.random

Получить управляемое случайное число.

```lua
ui.random() -> number
ui.random(upper) -> integer
ui.random(lower, upper) -> integer
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** 0..2 аргумента точно. Без аргументов — [0,1), кратные 2^-32. С одной границей lower=1. Границы — точные int32, включительно, lower<=upper. nil, дробь, строка — ошибка.

**Возврат:** Число; целый диапазон выбирается без modulo bias.

**Эффект и ограничения:** Меняет рабочий PCG32 и публикует один снимок RNG вместе с пакетом. До 1024 сырых выборок за обработчик, rejection sampling может расходовать больше одной на вызов. Восстанавливающий on_load не должен менять поток. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local die = ui.random(1, 6)
ui.set_text(refs.label, tostring(die))
```

**Связи:** [ui.seed_random](state-and-random.md#ui-seed-random), [ui.get_random_state](state-and-random.md#ui-get-random-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaRandomApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaRandomTests.cs).

<a id="ui-seed-random"></a>
## ui.seed_random

Инициализировать поток RNG воспроизводимым seed.

```lua
ui.seed_random(seed) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно один числовой целый аргумент [0,4294967295]; nil — ошибка.

**Возврат:** nil.

**Эффект и ограничения:** Расходует 2 сырых выборки и 2 единицы работы плюс стоимость вызова; не сбрасывает квоту обработчика. Host принимает состояние только с успешным пакетом. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.seed_random(42)
ui.set_text(refs.label, tostring(ui.random(6)))
```

**Связи:** [ui.random](state-and-random.md#ui-random), [ui.set_random_state](state-and-random.md#ui-set-random-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaRandomApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaRandomTests.cs).

<a id="ui-get-random-state"></a>
## ui.get_random_state

Получить точный снимок RNG без потери разрядов.

```lua
ui.get_random_state() -> string
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно ноль аргументов.

**Возврат:** Строка из 25 символов: pcg32-v1: и 16 строчных hex-цифр.

**Эффект и ограничения:** Чтение рабочего потока, выборок не делает; 25 символов результата расходуют общий строковый бюджет библиотеки. Можно вызвать в восстанавливающем on_load. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local snapshot = ui.get_random_state()
```

**Связи:** [ui.set_random_state](state-and-random.md#ui-set-random-state), [ui.random](state-and-random.md#ui-random). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaRandomApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaRandomTests.cs).

<a id="ui-set-random-state"></a>
## ui.set_random_state

Восстановить точный поток RNG из строки.

```lua
ui.set_random_state(snapshot) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно одна строка точного формата pcg32-v1:xxxxxxxxxxxxxxxx (lowercase hex); произвольное число вместо строки запрещено.

**Возврат:** nil.

**Эффект и ограничения:** Меняет рабочий поток, не восполняет квоту сырых выборок. В restore on_load итог должен совпасть с переданным host состоянием. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local saved = ui.get_random_state()
local a = ui.random()
ui.set_random_state(saved)
local b = ui.random() -- a == b
```

**Связи:** [ui.get_random_state](state-and-random.md#ui-get-random-state), [ui.seed_random](state-and-random.md#ui-seed-random). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaRandomApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaRandomTests.cs).
