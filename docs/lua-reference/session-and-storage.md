# Игровой сеанс, слоты, настройки и профиль

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Все функции — сервисы из on_load/on_event `ui`-обработчика и его модулей. Сценарному `game` недоступны. Для **каждой** записи: [модель пакета](execution-model.md#batch), [lifetime/загрузка](execution-model.md#lifetime), [service_result](events.md#service-result), [квоты](limits.md#storage). В восстанавливающем on_load мини-игры запрещены **все** сервисы, включая чтение.

Один пакет — **одна** сервисная команда; нельзя сочетать с `ui.close`, `ui.trigger` или завершением мини-игры. Сначала резервируется ответ и проверяется весь пакет; затем host выполняет сервис. Поэтому непосредственный return — ID запроса, а не данные. Даже чтение памяти приходит отдельным событием. Проверка формата аргумента — ошибка пакета; недоступность операции/Busy/диск — success=false с message. Ответ получает только исходный живой экземпляр и поколение. Успешные NewGame/MainMenu/EndSession/Load/Continue меняют lifetime и **не присылают успешного ответа в старую VM**.

Старые сервисные bindings кроме явно точных новых функций допускают лишние аргументы. new_game/save_slot принимают отсутствие и явный nil одинаково. Их строки читаются AsType MoonSharp (числа могут стать строками); get/set_persistent требуют точных строк. Значение set_setting — настоящий number/boolean, не строка.

### Слоты

ID: 1..64 ASCII из `a-z0-9_-`; Windows-имена con/prn/aux/nul/com0..com9/lpt0..lpt9 запрещены. `quicksave` — быстрый слот, отдельный путь. Не AuthorKey: точки не разрешены, первый символ может быть цифрой. Название не становится путём. 128 файлов вместе с quickslot, до 16 МиБ каждый. Подменённые symlink/reparse пути отклоняются.

Пути от LocalApplicationData/NovellMaker: в игре `Games/<project-id>/slots/<id>.json`, quick `Games/<project-id>/quicksave.json`; в F6 `Saves/<project-id>/slots/<id>.json`, quick `Saves/<project-id>.json`. Контейнер слота v2, вложенный GameSave v2; старый снимок v1 читается без перезаписи. Совместимость требует того же ProjectId и **точной редакции**, включая код/refs/интерфейс. Запись tmp/flush/atomic rename сохраняет прежний файл при отмене до публикации; отмена после публикации не откатывает файл.

Host принимает одну слотовую операцию одновременно; хранилище — одну активную и до 32 ожидающих, сериализация снимка — 2 активных +32 ожидающих на процесс. Слоты не содержат VM/замыкания/обычные экраны/профиль/аудиопозиции. Активная разрешённая мини-игра хранит Parameters/State/RNG и идентичность запуска; [барьеры сохранения](minigames.md).

### Профиль

Текущий профиль v3 принадлежит ProjectId, переживает редакцию проекта, Load/NewGame/MainMenu и не входит в слот/экспорт. Настройки и Data пишутся общим снимком; пути: `Games/<project-id>/profile.json` либо `Saves/<project-id>/profile.json` для F6. До 128 КиБ, backup и атомарная запись. Старые v1/v2 читаются с миграцией в памяти; запись только при явном изменении. Прежние достижения v2 становятся Data[id]="true"; функций get_achievement/unlock_achievement нет.

`persisted` означает, что **весь текущий профиль**, включая настройки, подтверждён на диске. success чтения памяти может сочетаться с persisted=false; при неудачной записи value/settings могут уже отражать новое состояние памяти. Профиль не защищён от редактирования игроком; процессов общей координации нет. Менять постоянные данные циклически каждый update не следует.

<a id="ui-new-game"></a>
## ui.new_game

Начать новое прохождение из меню.

```lua
ui.new_game() -> request_id
ui.new_game(sceneId) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** sceneId: отсутствует/nil — стартовая сюжетная сцена текущего запуска; иначе строковый GUID существующей сцены. Не назначает главное меню проекта.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="NewGame" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** В Menu создаёт новый сеанс, очищает старые экземпляры/переменные/операции. В Playing — отрицательный результат. Успех уничтожает исходный обработчик, поэтому service_result туда не доставляется. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.new_game()
```

**Связи:** [ui.main_menu](session-and-storage.md#ui-main-menu), [ui.end_session](session-and-storage.md#ui-end-session). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs).

<a id="ui-main-menu"></a>
## ui.main_menu

Вернуться в назначенное стартовое меню проекта.

```lua
ui.main_menu() -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="MainMenu" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Доступна из Playing при назначенном StartScreenSceneId. Иначе success=false. Успех меняет lifetime, исходный обработчик не получает ответ; профиль остаётся. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.main_menu()
```

**Связи:** [ui.new_game](session-and-storage.md#ui-new-game). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs).

<a id="ui-end-session"></a>
## ui.end_session

Завершить сеанс игры и закрыть окно runtime.

```lua
ui.end_session() -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="EndSession" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** При успешном завершении очищает операции и экраны; callback в старый обработчик не приходит. Запись профиля завершается по ограниченному shutdown-пути. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.end_session()
```

**Связи:** [ui.main_menu](session-and-storage.md#ui-main-menu). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs).

<a id="ui-continue-game"></a>
## ui.continue_game

Загрузить самый поздний совместимый слот.

```lua
ui.continue_game() -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="ContinueGame" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Фоновая проверка всех кандидатов, порядок SavedAtUtc по убыванию, затем ID ordinal. Если нет слота/идёт другая операция — success=false. Успешная загрузка меняет сеанс, ответ старому экземпляру не приходит. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.continue_game()
```

**Связи:** [ui.list_slots](session-and-storage.md#ui-list-slots), [ui.load_slot](session-and-storage.md#ui-load-slot). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs).

<a id="ui-list-slots"></a>
## ui.list_slots

Запросить список слотов и их совместимость.

```lua
ui.list_slots() -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="ListSlots" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Диск читается в фоне; результат содержит slots (до 128), см. events. Повреждённый слот остаётся строкой, не скрывает остальные. При IPC-переполнении только отображаемые строки сокращаются, message предупреждает; ID/статусы/редакции сохраняются. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.list_slots()
```

**Связи:** [ui.load_slot](session-and-storage.md#ui-load-slot), [ui.delete_slot](session-and-storage.md#ui-delete-slot). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs).

<a id="ui-save-slot"></a>
## ui.save_slot

Сохранить текущий согласованный снимок прохождения.

```lua
ui.save_slot() -> request_id
ui.save_slot(id) -> request_id
ui.save_slot(id, title) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** id: отсутствует/nil — новый slot-<GUID N>; строка допустимого ID — заменить этот слот. title: отсутствует/nil — имя сцены; иначе строка <=128, пустая допустима. ID см. правила ниже.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="SaveSlot" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Захват после commit пакета на host. Отказ в Menu, при pending сценарном Lua и неподходящем состоянии мини-игры. Ответ после атомарной публикации файла; slot_id содержит созданный/заданный ID. Busy/диск/квоты — success=false без повреждения прежнего файла. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.save_slot(nil, "Перед развилкой")
```

**Связи:** [ui.list_slots](session-and-storage.md#ui-list-slots), [ui.load_slot](session-and-storage.md#ui-load-slot). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs).

<a id="ui-load-slot"></a>
## ui.load_slot

Загрузить явно выбранный слот.

```lua
ui.load_slot(id) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** id: обязательная строка допустимого ID; nil — ошибка.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="LoadSlot" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Чтение/полная проверка/подготовка игрока в фоне; только успех заменяет сеанс без replay. Ошибка даёт success=false и сохраняет живое прохождение/pending. Успешный Load уничтожает исходный обработчик без service_result. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.load_slot("quicksave")
```

**Связи:** [ui.continue_game](session-and-storage.md#ui-continue-game), [ui.save_slot](session-and-storage.md#ui-save-slot). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs).

<a id="ui-delete-slot"></a>
## ui.delete_slot

Удалить файл слота по ID.

```lua
ui.delete_slot(id) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** id: обязательная строка допустимого ID; nil — ошибка.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="DeleteSlot" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Фоновое удаление одного файла, не прохождения/профиля/каталога. Отсутствующий обычный файл допускает успешное удаление; некорректные пути/доступ дают отрицательный результат. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.delete_slot("old-slot")
```

**Связи:** [ui.list_slots](session-and-storage.md#ui-list-slots). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs).

<a id="ui-get-settings"></a>
## ui.get_settings

Прочитать текущие настройки игрока.

```lua
ui.get_settings() -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Нет аргументов; лишние игнорируются.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="GetSettings" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Сервис читает память host; ответ всё равно отдельное событие, содержит settings и persisted. Может читать во время фоновой записи. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.get_settings()
```

**Связи:** [ui.set_setting](session-and-storage.md#ui-set-setting). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs).

<a id="ui-set-setting"></a>
## ui.set_setting

Изменить громкость, mute или полноэкранный режим игрока.

```lua
ui.set_setting(key, value) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** key: master_volume/sound_volume/effect_volume/voice_volume с числом [0,1] либо master_muted/sound_muted/effect_muted/voice_muted/fullscreen с boolean. nil и неверная пара ключ/тип — ошибка.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="SetSetting" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** После commit меняет настройки текущего окна и запускает запись профиля. Один изменяющий запрос профиля одновременно, Busy даёт success=false. При ошибке диска настройки в памяти могут оставаться изменёнными; persisted отличает их от подтверждённой записи. Отказ смены fullscreen не меняет профиль. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.set_setting("sound_volume", 0.5)
```

**Связи:** [ui.get_settings](session-and-storage.md#ui-get-settings), [ui.set_audio_volume](audio.md#ui-set-audio-volume). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Profiles/PlayerProfileTests.cs).

<a id="ui-get-persistent"></a>
## ui.get_persistent

Прочитать постоянную строку проекта из профиля.

```lua
ui.get_persistent(key) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** key: точная строка AuthorKey [a-z][a-z0-9_.-]{0,63}; nil/число — ошибка.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="GetPersistent" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Читает текущую память без повторного диска. Ответ: key, value:string|nil, persisted:boolean; success чтения не означает, что весь профиль записан. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.get_persistent("achievement.first")
```

**Связи:** [ui.set_persistent](session-and-storage.md#ui-set-persistent). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/PersistentDataLuaTests.cs).

<a id="ui-set-persistent"></a>
## ui.set_persistent

Записать или удалить постоянную строку проекта.

```lua
ui.set_persistent(key) -> request_id
ui.set_persistent(key, value) -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** key: точный AuthorKey; value: точная строка <=1024 либо отсутствует/nil для удаления. "" — сохраняемое значение. До 256 ключей, 16384 символов ключей+значений в профиле.

**Возврат:** Сразу request_id: строка GUID N. Позднее service_result с operation="SetPersistent" и этим request_id; исключения доставки описаны ниже.

**Эффект и ограничения:** Проверка кандидата атомарна. Изменяет память после commit, success записи приходит после фонового сохранения общего профиля. Ошибка не откатывает принятую память; ответ value показывает её. Повтор того же значения повторяет неудавшуюся запись. Профиль переживает NewGame/Load/MainMenu и закрытие экрана, callback — нет. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.set_persistent("achievement.first", "true")
```

**Связи:** [ui.get_persistent](session-and-storage.md#ui-get-persistent), [ui.trigger](input-and-triggers.md#ui-trigger). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/PersistentDataLuaTests.cs).
