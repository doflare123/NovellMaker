# Анимации компонентов

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Контекст: только активный on_load/on_event `ui` и функции модулей, вызванные им. `game.move` — другая система. Общие правила каждой функции: [пакеты](execution-model.md#batch), [lifetime](execution-model.md#lifetime), [restore](execution-model.md#restore), [квоты](limits.md#animations), [animation_result](events.md#animation-result). Описания живут **один вызов**, ID запущенной группы — до её результата; ни то ни другое не хранится в слот.

Треки меняют только x/y/width/height/anchor_x/anchor_y/pivot_x/pivot_y/scale_x/scale_y/rotation/opacity в [диапазонах геометрии](components.md). Цвет, текст, parent, order и clip_children не анимируются. Easing: `linear`, `in_quad`, `out_quad`, `in_out_quad`, `in_cubic`, `out_cubic`, `in_out_cubic`. Интерполяция выполняется host без Lua каждый кадр; исходное значение захватывается в начале соответствующего трека.

Восстанавливающий on_load может вычислить чистое описание, но не запускать/отменять анимации. Пауза, закрытие/ошибка и загрузка следуют [общему lifecycle](execution-model.md#lifetime). Закрытие не обязано доставлять оставшиеся результаты в исчезнувшую VM. Мини-игра не сохраняется до завершения и обработки её анимационных результатов. Примеры требуют refs.label собственного узла и его допустимой геометрии.

<a id="ui-tween"></a>
## ui.tween

Описать интерполяцию свойств собственного узла.

```lua
ui.tween(node, properties, seconds) -> description
ui.tween(node, properties, seconds, easing) -> description
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно 3 или 4 аргумента. properties: непустая таблица числовых целей, только анимируемые поля геометрии ниже. seconds: конечное >0 и <=3600; easing по умолчанию linear, явный nil — ошибка. Все цели должны соответствовать геометрическим диапазонам.

**Возврат:** Непрозрачная строка description, действительная только в текущем вызове обработчика.

**Эффект и ограничения:** Немедленно создаёт описание, не запускает движение и не расходует команду. До 128 описаний/обработчик, 64 треков/описание. Масштаб не может сменить знак через 0. Сложная мировая геометрия дополнительно проверяется при animate. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local t = ui.tween(refs.label, {x=200, opacity=0.5}, 1, "out_quad")
ui.animate(t)
```

**Связи:** [ui.animate](animations.md#ui-animate), [ui.sequence](animations.md#ui-sequence), [ui.parallel](animations.md#ui-parallel). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).

<a id="ui-sequence"></a>
## ui.sequence

Соединить описания последовательно.

```lua
ui.sequence(children) -> description
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно одна таблица: непустой плотный список до 64 описаний текущего обработчика; nil/старые описания/пропуски запрещены.

**Возврат:** Новое description текущего обработчика.

**Эффект и ограничения:** Сдвигает каждый дочерний план на суммарную длительность предыдущих. До 64 треков и <=3600 секунд всего, до 128 описаний вместе с дочерними. Команду не создаёт. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local a = ui.tween(refs.label, {x=200}, 1)
local b = ui.tween(refs.label, {opacity=0.5}, 1)
ui.animate(ui.sequence({a,b}))
```

**Связи:** [ui.tween](animations.md#ui-tween), [ui.animate](animations.md#ui-animate). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).

<a id="ui-parallel"></a>
## ui.parallel

Соединить описания параллельно.

```lua
ui.parallel(children) -> description
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно одна таблица: непустой плотный список до 64 описаний текущего обработчика; nil/старые описания/пропуски запрещены.

**Возврат:** Новое description текущего обработчика.

**Эффект и ограничения:** Начинает все дочерние планы в 0; пересекающиеся интервалы одного свойства одного узла запрещены при проверке плана. До 64 треков и <=3600 секунд всего, до 128 описаний вместе с дочерними. Команду не создаёт. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local a = ui.tween(refs.label, {x=200}, 1)
local b = ui.tween(refs.label, {opacity=0.5}, 1)
ui.animate(ui.parallel({a,b}))
```

**Связи:** [ui.tween](animations.md#ui-tween), [ui.animate](animations.md#ui-animate). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).

<a id="ui-animate"></a>
## ui.animate

Запустить описанную анимационную группу.

```lua
ui.animate(description) -> animation_id
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно одно description текущего обработчика. nil/чужое/из прошлого вызова — ошибка.

**Возврат:** ID операции (32 hex). Завершение — animation_result с тем же animation_id.

**Эффект и ограничения:** Создаёт команду, запуск после полного commit. До 16 групп и 256 треков на экземпляр. Конфликтующая новая группа отменяет целиком старую (replaced). Резерв события входит в 128. В pause и при unsettled Lua этого экземпляра интерполяция стоит; слоты не хранят анимации. Restore on_load запрещает запуск. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local id = ui.animate(ui.tween(refs.label, {x=200}, 1))
```

**Связи:** [ui.cancel_animation](animations.md#ui-cancel-animation), [ui.tween](animations.md#ui-tween). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).

<a id="ui-cancel-animation"></a>
## ui.cancel_animation

Остановить свою анимационную группу на текущем значении.

```lua
ui.cancel_animation(animation_id) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно одна строка непустого GUID формата N (32 hex).

**Возврат:** nil; для живой группы — позднее animation_result, status=cancelled, reason=requested.

**Эффект и ограничения:** Команда пакета. Отсутствующий/уже завершённый/не принадлежащий этому экземпляру корректный ID — no-op. Не откатывает геометрию. Restore on_load запрещает отмену. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local id=ui.animate(ui.tween(refs.label, {x=200}, 1))
ui.cancel_animation(id)
```

**Связи:** [ui.animate](animations.md#ui-animate). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).
