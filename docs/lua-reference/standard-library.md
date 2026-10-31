# Ограниченная стандартная библиотека

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Доступна **только** обработчикам ui и импортированным модулям, в том числе при загрузке исходника, on_load, on_event и восстанавливающем on_load. В сценарном game не установлена. Это собственный allowlist поверх `CoreModules.None`, а не все библиотеки Lua 5.2/MoonSharp.

Общие правила **каждой** функции: [локальная VM и восстановление](execution-model.md#lifetime), [атомарность](execution-model.md#batch), [квоты](limits.md#standard). Стандартные функции выполняются немедленно, не ставят host-команды, сами ничего не сохраняют в слот/профиль. При превышении бюджета/неверном типе возникает ошибка Lua, весь игровой пакет обработчика не применяется; локальные мутации VM не являются сохранённым состоянием. На загрузку исходника и на каждый обработчик бюджет сбрасывается, но все импорты/модули текущего вызова делят его.

Строка на входе/выходе <=8192 единиц UTF-16, сумма строковых результатов <=32768. Все числа, проверяемые функцией, должны быть конечными. `int32` — целое [-2147483648,2147483647]. Типы точные, implicit tonumber/tostring не делается. Число аргументов точное в пределах сигнатуры: **отсутствие optional выбирает default, явный nil даёт ошибку**, если запись не говорит иначе. Дополнительные аргументы не игнорируются. Стоимость native работы <=65536 единиц/вызов обработчика, включая модули; это отдельный предел от 20000 Lua-инструкций.

Табличные функции проверяют <=1024 **физических** записей, в том числе удалённые tombstones MoonSharp. Поэтому активно дописываемую/очищаемую таблицу лучше пересоздавать. Для списков дополнительно требуются все живые ключи 1..N без пропусков. Метатаблицы не допускаются. Сканирование расходует по единице на физическую запись, включая пропущенные nil; каждая функция ещё минимум 1 единицу. В отличие от этого преобразование в Value пропускает tombstones.

В примерах с ui.set_text заранее назначьте refs.label обычному Text собственного холста. Остальные примеры библиотеки не требуют ресурсов.

### Данные и недоступные возможности

`math.pi` — единственная числовая константа math, около 3.141592653589793; не функция. Строковый синтаксис `s:sub(...)` не следует считать подключённым стандартным string metatable: вызывайте `string.sub(s,...)`.

Отсутствуют глобальные `_G`, `_VERSION`, `assert`, `error`, `pcall`, `xpcall`, `print`, `next`, `select`, `unpack`, `rawequal`, `rawget`, `rawset`, `getmetatable`, `setmetatable`, `load`, `loadstring`, `loadfile`, `dofile`, `require`, `collectgarbage`; таблицы `io`, `os`, `debug`, `package`, `coroutine`, `bit32`, `utf8`, `clr`, `luanet` не установлены. Нет файлового require, CLR reflection, произвольной сети, системных часов, создания coroutine/yield или динамической загрузки кода. В math нет random/randomseed/huge (используйте ui.random); в string нет byte/char/format/dump/gsub/match/gmatch/patterns; в table нет sort/pack/unpack. Всё не перечисленное в алфавитном указателе не обещано. Язык по-прежнему позволяет операторы, циклы, таблицы, Lua-функции/замыкания, generic for; это не дополнительные зарегистрированные API-функции.

<a id="type"></a>
## type

Узнать тип Lua-значения.

```lua
type(value) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно один аргумент, любой, в том числе явный nil; отсутствие — ошибка.

**Возврат:** nil/boolean/number/string/table/function/userdata как строка. ui.null — userdata. Lua- и callback-функции обе function.

**Эффект и ограничения:** Немедленно; длина результата расходует строковый бюджет. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local t = type(ui.null) -- "userdata"
```

**Связи:** [tostring](standard-library.md#tostring), `ui.null`. [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="tostring"></a>
## tostring

Преобразовать скаляр в текст.

```lua
tostring(value) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно один nil/boolean/конечное number/string <=8192. Таблицы, функции, ui.null запрещены; метаметоды не вызываются.

**Возврат:** "nil", "true", "false", число в invariant формате G либо исходная строка.

**Эффект и ограничения:** Немедленно; размер результата входит в квоты текста и работы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
ui.set_text(refs.label, tostring(12.5))
```

**Связи:** [type](standard-library.md#type), [tonumber](standard-library.md#tonumber). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="tonumber"></a>
## tonumber

Прочитать десятичное число из строки.

```lua
tonumber(value) -> number | nil
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно один аргумент. Число должно быть конечным; строка <=8192, формат invariant NumberStyles.Float (пробелы, знак, десятичная точка, экспонента). Второго основания нет.

**Возврат:** Число либо nil при неподходящем типе/неразбираемой строке/строковом NaN или Infinity. Числовые NaN/Infinity — ошибка.

**Эффект и ограничения:** Немедленно; дополнительно length строки единиц работы, даже при неуспехе. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = tonumber(" 1.25e2 ") -- 125
```

**Связи:** [tostring](standard-library.md#tostring). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="pairs"></a>
## pairs

Перебирать снимок пар таблицы.

```lua
pairs(value) -> iterator
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одна таблица без метатаблицы, <=1024 физических записей (включая удалённые tombstones). nil — ошибка.

**Возврат:** Одна callback-функция: iterator() -> key,value либо nil в конце. Это не стандартная тройка iterator,state,key; generic for работает. Порядок не обещан.

**Эффект и ограничения:** Копирует пары на момент вызова; последующие замены значений исходной таблицы не видны. Вложенные таблицы остаются ссылочными Lua-значениями. Стоимость: 1 + физические записи; каждый iterator(), даже после конца, ещё 1. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local total = 0
for k,v in pairs({a=2,b=3}) do total=total+v end -- 5
```

**Связи:** [ipairs](standard-library.md#ipairs). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="ipairs"></a>
## ipairs

Перебирать снимок плотного списка.

```lua
ipairs(value) -> iterator
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одна таблица без метатаблицы с ключами 1..N без пропусков и посторонних ключей, <=1024 физических записей.

**Возврат:** Одна функция iterator() -> index,value или nil. Пропуски отвергаются, а не завершают обход на первом nil.

**Эффект и ограничения:** Снимок значений на момент вызова; стоимость как pairs. Итератор живёт в VM, не сериализуется. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
for i,v in ipairs({"a","b"}) do ui.log("info", v) end
```

**Связи:** [pairs](standard-library.md#pairs), [table.insert](standard-library.md#table-insert). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-abs"></a>
## math.abs

Модуль числа.

```lua
math.abs(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.abs(-3) -- 3
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-ceil"></a>
## math.ceil

Округление вверх.

```lua
math.ceil(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.ceil(1.2) -- 2
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-floor"></a>
## math.floor

Округление вниз.

```lua
math.floor(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.floor(-1.2) -- -2
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-sqrt"></a>
## math.sqrt

Квадратный корень.

```lua
math.sqrt(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. x>=0.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.sqrt(9) -- 3
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-sin"></a>
## math.sin

Синус угла в радианах.

```lua
math.sin(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.sin(0) -- 0
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-cos"></a>
## math.cos

Косинус угла в радианах.

```lua
math.cos(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.cos(0) -- 1
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-tan"></a>
## math.tan

Тангенс угла в радианах.

```lua
math.tan(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.tan(0) -- 0
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-asin"></a>
## math.asin

Арксинус в радианах.

```lua
math.asin(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. -1<=x<=1.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.asin(0) -- 0
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-acos"></a>
## math.acos

Арккосинус в радианах.

```lua
math.acos(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. -1<=x<=1.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.acos(1) -- 0
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-atan"></a>
## math.atan

Арктангенс в радианах.

```lua
math.atan(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.atan(0) -- 0
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-exp"></a>
## math.exp

Экспонента e^x.

```lua
math.exp(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.exp(0) -- 1
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-deg"></a>
## math.deg

Перевод радиан в градусы.

```lua
math.deg(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.deg(math.pi) -- 180
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-rad"></a>
## math.rad

Перевод градусов в радианы.

```lua
math.rad(x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное number; строки/nil запрещены. Результат должен оставаться конечным.

**Возврат:** Конечное number. Недопустимая область или переполнение .NET Math → ошибка Lua вместо NaN/Infinity.

**Эффект и ограничения:** Немедленно; 1 единица общего бюджета работы. Нет команды/host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.rad(180) -- math.pi
```

**Связи:** `math.pi`, [math.atan2](standard-library.md#math-atan2). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-atan2"></a>
## math.atan2

Арктангенс с учётом квадранта, результат в радианах.

```lua
math.atan2(y, x) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно два конечных числа y,x; (0,0) принимается .NET Math.Atan2.

**Возврат:** Конечное number.

**Эффект и ограничения:** Немедленно; 1 единица работы, без host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.atan2(0,1) -- 0
```

**Связи:** [math.modf](standard-library.md#math-modf). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-pow"></a>
## math.pow

Возведение x в степень y.

```lua
math.pow(x, y) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно два конечных числа. Недействительная вещественная степень и переполнение — ошибка.

**Возврат:** Конечное number.

**Эффект и ограничения:** Немедленно; 1 единица работы, без host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.pow(2,3) -- 8
```

**Связи:** [math.modf](standard-library.md#math-modf). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-fmod"></a>
## math.fmod

Остаток от деления со знаком делимого.

```lua
math.fmod(x, y) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно два конечных числа; y=0 даёт ошибку конечности результата. В отличие от оператора Lua %, отрицательный x даёт отрицательный остаток.

**Возврат:** Конечное number.

**Эффект и ограничения:** Немедленно; 1 единица работы, без host-эффекта. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.fmod(-7,3) -- -1
```

**Связи:** [math.modf](standard-library.md#math-modf). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-log"></a>
## math.log

Логарифм натуральный либо по основанию.

```lua
math.log(x) -> number
math.log(x, base) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно 1..2 конечных числа. По умолчанию e лишь при отсутствии base; явный nil — ошибка. x>0; основания/результаты проверяются фактической конечностью Math.Log, не отдельной Lua-таблицей диапазонов.

**Возврат:** Конечное число; log(0), log(2,1), NaN/Infinity — ошибка.

**Эффект и ограничения:** Немедленно; 1 единица работы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.log(8,2) -- 3
```

**Связи:** [math.exp](standard-library.md#math-exp). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-modf"></a>
## math.modf

Разделить число на целую и дробную части.

```lua
math.modf(x) -> integerPart, fractionPart
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одно конечное число; целая часть — усечение к нулю, не floor.

**Возврат:** Два number; дробная часть сохраняет знак.

**Эффект и ограничения:** Немедленно; 1 единица работы. integerPart по смыслу целое, но не ограничено int32. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local a,b = math.modf(-1.25) -- -1, -0.25
```

**Связи:** [math.floor](standard-library.md#math-floor), [math.fmod](standard-library.md#math-fmod). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-min"></a>
## math.min

Минимум конечных чисел.

```lua
math.min(x, ...) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно 1..64 числовых аргумента; nil, строки, пустой вызов запрещены.

**Возврат:** Одно конечное число.

**Эффект и ограничения:** Немедленно; 1 + число аргументов единиц работы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.min(4,-1,2) -- -1
```

**Связи:** [math.abs](standard-library.md#math-abs). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="math-max"></a>
## math.max

Максимум конечных чисел.

```lua
math.max(x, ...) -> number
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно 1..64 числовых аргумента; nil, строки, пустой вызов запрещены.

**Возврат:** Одно конечное число.

**Эффект и ограничения:** Немедленно; 1 + число аргументов единиц работы. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = math.max(4,-1,2) -- 4
```

**Связи:** [math.abs](standard-library.md#math-abs). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-len"></a>
## string.len

Длина строки в единицах UTF-16.

```lua
string.len(text) -> integer
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одна точная строка <=8192; числа не приводятся.

**Возврат:** Длина UTF-16, не UTF-8 байты, не графемы и не число Unicode scalar values.

**Эффект и ограничения:** Немедленно; 1 единица работы; строковый результат не создаётся. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local n = string.len("абв") -- 3
```

**Связи:** [string.sub](standard-library.md#string-sub). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-sub"></a>
## string.sub

Выделить подстроку по включительным индексам.

```lua
string.sub(text, first) -> string
string.sub(text, first, last) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** text<=8192; first/last — int32. Индекс с 1; отрицательный от конца. first снизу ограничивается 1, last сверху длиной. last отсутствует → длина; явный nil — ошибка.

**Возврат:** Строка; обратный/пустой диапазон → "".

**Эффект и ограничения:** Немедленно; 1 + длина результата единиц работы и длина результата в общий бюджет текста. Работает по UTF-16 и может разрезать суррогатную пару. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = string.sub("abcdef",2,-2) -- "bcde"
```

**Связи:** [string.len](standard-library.md#string-len). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-lower"></a>
## string.lower

Перевести регистр в нижний.

```lua
string.lower(text) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одна точная строка <=8192; nil — ошибка.

**Возврат:** Новая строка той же длины; регистр invariant .NET, reverse не сохраняет целостность emoji/суррогатных пар.

**Эффект и ограничения:** Немедленно; 1 + длина строки единиц работы и эта длина в текстовый бюджет. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = string.lower("ПРИВЕТ")
```

**Связи:** [string.sub](standard-library.md#string-sub). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-upper"></a>
## string.upper

Перевести регистр в верхний.

```lua
string.upper(text) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одна точная строка <=8192; nil — ошибка.

**Возврат:** Новая строка той же длины; регистр invariant .NET, reverse не сохраняет целостность emoji/суррогатных пар.

**Эффект и ограничения:** Немедленно; 1 + длина строки единиц работы и эта длина в текстовый бюджет. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = string.upper("привет")
```

**Связи:** [string.sub](standard-library.md#string-sub). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-reverse"></a>
## string.reverse

Развернуть порядок единиц UTF-16.

```lua
string.reverse(text) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно одна точная строка <=8192; nil — ошибка.

**Возврат:** Новая строка той же длины; регистр invariant .NET, reverse не сохраняет целостность emoji/суррогатных пар.

**Эффект и ограничения:** Немедленно; 1 + длина строки единиц работы и эта длина в текстовый бюджет. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = string.reverse("абв")
```

**Связи:** [string.sub](standard-library.md#string-sub). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-rep"></a>
## string.rep

Повторить строку с разделителем.

```lua
string.rep(text, count) -> string
string.rep(text, count, separator) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** text/ separator: точные строки <=8192; count: int32 [0,8192]. separator отсутствует → ""; nil — ошибка.

**Возврат:** Строка длиной len(text)*count+len(separator)*max(0,count-1), не более 8192; count=0 → "".

**Эффект и ограничения:** Проверка до выделения результата; стоимость 1 + count + длина результата, текстовый бюджет — длина результата. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s = string.rep("ab",3,":") -- "ab:ab:ab"
```

**Связи:** [table.concat](standard-library.md#table-concat). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="string-find"></a>
## string.find

Найти буквальную подстроку без Lua patterns.

```lua
string.find(text, needle) -> first, last | nil
string.find(text, needle, first) -> first, last | nil
string.find(text, needle, first, true) -> first, last | nil
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно 2..4 аргумента. Обе строки <=8192; first int32, default 1 только при отсутствии; отрицательный от конца, снизу ограничивается 1. Четвёртый аргумент при наличии обязан быть true; false/nil — ошибка.

**Возврат:** Индексы UTF-16 с 1, конец включительно, либо один nil. Пустой needle на позиции length+1 даёт length+1,length.

**Эффект и ограничения:** Ordinal поиск .NET; стоимость 1+len(text)*max(1,len(needle)) даже при быстром совпадении/неуспехе. Большой поиск может превысить 65536, хотя обе строки короче 8192. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local a,b = string.find("a.b.c", ".", 2, true) -- 2,2
```

**Связи:** [string.sub](standard-library.md#string-sub). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="table-insert"></a>
## table.insert

Вставить непустое значение в плотный список.

```lua
table.insert(list, value) -> nil
table.insert(list, position, value) -> nil
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно 2..3 аргумента. Таблица без метатаблицы, все живые ключи 1..N, физические записи <=1024. position int32 [1,N+1], default N+1. value любое кроме nil; N<1024.

**Возврат:** nil; исходная Lua-таблица изменяется со сдвигом элементов.

**Эффект и ограничения:** Немедленная локальная мутация, не запись host state. Сначала бюджет 1 + физические записи + N+1, затем изменение; для публикации используйте set_state. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local t={"b"}
table.insert(t,1,"a") -- {"a","b"}
```

**Связи:** [table.remove](standard-library.md#table-remove), [ui.set_state](state-and-random.md#ui-set-state). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="table-remove"></a>
## table.remove

Удалить элемент плотного списка.

```lua
table.remove(list) -> any | nil
table.remove(list, position) -> any
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** Ровно 1..2 аргумента; таблица как insert. position int32 [1,N], default N. nil вместо position — ошибка. Пустой список без position допускается.

**Возврат:** Удалённое значение; для пустого списка без position — nil. Сдвигает хвост в исходной таблице.

**Эффект и ограничения:** Немедленно; стоимость 1 + физические записи + N (для пустого default только 1+сканирование). Tombstones могут оставаться и учитываться следующими библиотечными вызовами. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local t={"a","b"}
local last=table.remove(t) -- "b"
```

**Связи:** [table.insert](standard-library.md#table-insert). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="table-concat"></a>
## table.concat

Соединить диапазон плотного списка в строку.

```lua
table.concat(list) -> string
table.concat(list, separator) -> string
table.concat(list, separator, first) -> string
table.concat(list, separator, first, last) -> string
```

**Доступность:** ui при загрузке исходника, on_load/on_event, модуль, мини-игра; restore допустим. В game отсутствует.

**Аргументы и ошибки:** list: таблица как insert; separator точная строка, default ""; first int32 [1,N+1], default 1; last int32 [0,N], default N. Default только при отсутствии, nil — ошибка. Выбранные элементы string/конечные number.

**Возврат:** Строка <=8192, пустой диапазон → "". Числа invariant G.

**Эффект и ограничения:** Немедленно; 1+физические записи+длина результата единиц работы, длина результата в текстовый бюджет; не меняет таблицу. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local s=table.concat({"a",2,"c"},"-",2,3) -- "2-c"
```

**Связи:** [tostring](standard-library.md#tostring), [string.rep](standard-library.md#string-rep). [Регистрация и аргументы](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).
