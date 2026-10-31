# Квоты и область действия

[Оглавление](index.md) · [Модель исполнения](execution-model.md)

Числа ниже взяты из текущих констант/проверок. «Символы» — единицы UTF-16 C#, если явно не указаны байты. Это разные бюджеты: прохождение preflight или syntax не гарантирует, что хватит инструкций, native работы, данных или очереди. Ошибка квоты обычно отклоняет пакет; конкретные отказы сервиса/доставка — у функций и в [событиях](events.md).

## Исходник, VM и транспорт

| Область | Предел | Источник |
| --- | --- | --- |
| Один Lua-исходник | 32768 символов | LuaActionScript.MaximumSourceLength |
| Preflight одного файла | 4096 лексических единиц, 64 суммарных уровня скобок/блоков, 128 конструкций, 256 операторных символов/слов | LuaLimits.CheckSource; строки/комментарии исключены, это консервативный фильтр, не точный AST parser |
| Одно вычисление исходника / один обработчик | 20000 Lua-инструкций | AutoYieldCounter; yield трактуется как превышение, не продолжение |
| Все Lua-worker host-процесса | максимум 2 процесса и 34 занятых admission-места суммарно (2+32); сценарный deadline 5000 мс | LuaWorkerClient/Slots/Transport |
| Постоянная ui-беседа host-процесса | максимум 1, удерживает одно общее process/admission-место на свой lifetime; сценарный worker может работать рядом | LuaWorkerSlots.ScreenSessions, ScreenLuaConnection |
| Сценарный RPC, размер | запрос <=16 МиБ, ответ <=1 МиБ | LuaWorkerClient |
| ui IPC | один кадр <=1 МиБ в каждом направлении; общий срок события 5 секунд с очередью/подготовкой | ScreenLuaProtocol/Session |
| Память worker | managed heap 64 МиБ; на Windows committed memory процесса 256 МиБ | LuaWorkerProcess, LuaWorkerJob |
| Очистка ui connection | ограниченная, ожидание до 3 секунд вне кадра | ScreenLuaSession.Retire/DisposeAsync |
| Ожидание очистки сценарных pending при DisposeAsync | до 2 секунд | ScenarioPlayer.Lua |

Источники: [LuaLimits](../../src/NovellMaker.Core/Execution/LuaLimits.cs), [VM](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), [client](../../src/NovellMaker.Core/Execution/LuaWorkerClient.cs), [process](../../src/NovellMaker.Core/Execution/LuaWorkerProcess.cs), [connection](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaConnection.cs). Тесты: [границы](../../tests/NovellMaker.Core.Tests/Execution/LuaLimitBoundaryTests.cs), [транспорт](../../tests/NovellMaker.Core.Tests/Execution/LuaWorkerTransportTests.cs).

<a id="scenario"></a>
## Сценарий

| Область | Предел |
| --- | --- |
| Результат одного Lua-блока | 256 команд |
| Вся ожидающая сценарная очередь | 4096 команд, 524288 символов учитываемых данных; параметры мини-игры учитываются JSON-байтами |
| Проход без игрового ожидания | 2048 действий; до 32 Lua-вызовов /5 секунд активного прохода |
| Tick с разбивкой по ожиданиям | до 2048 шагов |
| Переменные сюжета | 256; имя непустое/непробельное <=128, значение <=8192; ключи+значения <=131072 |
| Ссылки блока/ресурса | 256, имя <=128; GUID непуст |
| Выбор | 1..8 вариантов; текст непустой/непробельный <=8192, ID уникальны |
| Продолжительность wait/move | конечные секунды [0,86400] |
| Placement | x/y [-1,2], scale [0.05,5], integer layer [-1000,1000] |
| Hotspot | x/y>=0, width/height>0, x+width<=1, y+height<=1; label<=8192 |

Источники: [ScenarioProgram](../../src/NovellMaker.Core/Execution/ScenarioProgram.cs), [ScenarioPlayer](../../src/NovellMaker.Core/Execution/ScenarioPlayer.cs), [LuaLimits](../../src/NovellMaker.Core/Execution/LuaLimits.cs). Тесты: [сценарий](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs), [placement](../../tests/NovellMaker.Core.Tests/Execution/PlacementTests.cs).

<a id="components"></a>
## Компоненты

| Область | Предел |
| --- | --- |
| Один ui-пакет | 256 команд, включая одну добавляемую метадату RNG |
| Один холст | 1 корень; 1024 узла; depth 32 |
| Legacy migration exception | при распознанной тройке старых CSSPanel/Speaker/Text до 4 дополнительных узлов и +1 depth; не общий новый авторский лимит |
| ID узлов одного экземпляра | живые + retired <=4096, удалённые не переиспользуются |
| Стек | 32 экземпляра, 4096 живых узлов сеанса |
| Текст одного узла/set_text | 8192 |
| Текст дерева / сумма текстов set_text пакета | каждый <=131072 |
| Имя узла | <=128 |
| Geometry | [полная таблица диапазонов](components.md); мировые углы <=1e8 по модулю, модуль determinant>=1e-12 |
| Ссылки refs/audio | каждая таблица <=256, имена <=128 |
| Растер обычного Text/Button | видимая локальная область: сторона <=2048 и площадь <=262144 пикселей; привязанный диалог может разбиваться на полосы <=262144 |
| Кеш растров текста всех слоёв | 4194304 пикселя; до 4 новых текстур/262144 пикселей подготовки за кадр |
| Кеш изображений runtime | 128 МиБ/256 ресурсов; prefetch <=64 МиБ/16 hints; upload <=1 МиБ за кадр |

Геометрически допустимый очень большой Text/Button может превысить бюджет растеризации: это диагностика отображения, а не отказ Lua-пакета. Кнопка сохраняет заливку и hit-test без подписи. Скрытые/обрезанные области отсекаются до растра; deferred подготовка не означает нарушение порядка команд.

Источники: [definition](../../src/NovellMaker.Core/Screens/ScreenDefinition.cs), [tree](../../src/NovellMaker.Core/Screens/ScreenTree.cs), [stack](../../src/NovellMaker.Core/Screens/ScreenStack.cs), [protocol](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs), [растры](../../src/NovellMaker.Runtime/ScreenTextureCache.cs), [image cache](../../src/NovellMaker.Runtime/RuntimeImageCache.cs). Тесты: [ScreenTreeTests](../../tests/NovellMaker.Core.Tests/Screens/ScreenTreeTests.cs), [VisualScreenTests](../../tests/NovellMaker.Core.Tests/Screens/VisualScreenTests.cs), [ScreenTextureCacheTests](../../tests/NovellMaker.Runtime.Tests/ScreenTextureCacheTests.cs).

<a id="values"></a>
## Value, state, RNG и мини-игра

| Область | Предел |
| --- | --- |
| Одно сериализуемое Value | контейнеры depth<=16; значений с корнем/контейнерами <=1024 |
| Строки/ключи Value | строка<=8192, ключ<=128, сумма<=32768; корректный Unicode |
| Канонический JSON одного Value | <=65536 UTF-8 байт с escaping |
| Все set_state одного пакета | сумма JSON<=65536 байт, даже если последняя запись заменила предыдущую |
| Один обработчик RNG | <=1024 сырых uint32 выборок; seed расходует 2 и не сбрасывает счётчик |
| Границы random | int32 включительно; seed uint32 |
| RNG snapshot | pcg32-v1: +16 lowercase hex, 25 символов |
| Схема мини-игры | <=64 полей Object, непустые/непробельные ключи<=128; типы непосредственных полей |
| UpdatesPerSecond | [1,20], default 5; накопленная дельта<=1 секунда |
| SaveDisabledReason | непустая/непробельная <=256 |
| Message итога мини-игры | <=1024 |

Источники: [Value](../../src/NovellMaker.Core/Scripting/LuaValue.cs), [projection](../../src/NovellMaker.Core/Scripting/LuaValueProjection.cs), [RNG](../../src/NovellMaker.Core/Scripting/LuaRandomApi.cs), [schema](../../src/NovellMaker.Core/Minigames/MinigameDefinition.cs). Тесты: [state](../../tests/NovellMaker.Core.Tests/Screens/PersistentDataLuaTests.cs), [RNG](../../tests/NovellMaker.Core.Tests/Screens/LuaRandomTests.cs), [minigame](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs).

<a id="modules"></a>
## Модули

| Область | Предел |
| --- | --- |
| Достижимый граф с корневым обработчиком | 32 ресурса, depth 16 |
| Код всего графа | 65536 символов; каждый файл также <=32768 и проходит preflight |
| Импорты | <=32 aliases одного файла, <=256 связей графа |
| Вычисление импорта | общий бюджет инструкций/библиотеки вызывающего load/обработчика, не новый бюджет |

Циклы отвергаются даже до фактического импорта. Кеш на экземпляр VM, не процесс/проект. Источник: [LuaModuleLibrary](../../src/NovellMaker.Core/Scripting/LuaModuleLibrary.cs). Тесты: [LuaModuleTests](../../tests/NovellMaker.Core.Tests/Screens/LuaModuleTests.cs).

<a id="events"></a>
## События и триггеры

| Область | Предел |
| --- | --- |
| Очередь ScreenLuaSession | 128 с резервами сервисов/анимаций/звука |
| Причинная цепь | 32 поколения, сервисные продолжения не обнуляют |
| Триггеры пакета | 16 |
| AuthorKey | [a-z][a-z0-9_.-]{0,63} |
| Trigger data | <=1024, optional/nil допустим |
| Сервис пакета | один, несовместим с trigger/close/minigame completion |
| Координаты события | конечные, |x|/|y|<=100000 |
| seconds | [0,1] |
| Message сервиса / Diagnostic строки слота | <=1024 |

Источники: [AuthorTrigger](../../src/NovellMaker.Core/Triggers/AuthorTrigger.cs), [session](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.cs), [protocol](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs). Тесты: [session](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaSessionTests.cs).

<a id="animations"></a>
## Анимации

| Область | Предел |
| --- | --- |
| Описания за обработчик | 128 вместе с tween и составными группами |
| Треков описания/запущенной группы | 64 |
| Дочерних описаний в sequence/parallel | 1..64, плотный список |
| Длительность плана | >0, <=3600 секунд; каждый трек start>=0, seconds>0 |
| Живых групп экземпляра | 16 |
| Живых треков экземпляра | 256 |
| Active+готовые результаты аниматора | <=128, также общий бюджет очереди сеанса |
| Easing | 7 перечисленных имён, overshoot-эффектов нет |
| reason события | <=128, полный набор производителя в events |

Источник: [аниматор](../../src/NovellMaker.Core/Screens/Animations/ScreenAnimation.cs), [Lua API](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs). Тесты: [AnimationTests](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs).

<a id="audio"></a>
## Звук

| Область | Предел |
| --- | --- |
| AudioService | 32 одновременно играющих источника, общий с сюжетом |
| PCM WAV | signed 16-bit, mono/stereo, 8000..48000 Гц |
| Импорт WAV | файл <=2 ГиБ; RIFF <=1024 chunks, размер каждого chunk<=int32 max, data непустой/кратный размеру sample frame |
| Усиление источника/группы/Master | конечное [0,1] |
| Источники Lua | каждый резервирует один audio_result в общем лимите 128 |
| Message audio_result | <=1024 |

Источник: [AudioService](../../src/NovellMaker.Core/Audio/AudioService.cs), [PcmWave](../../src/NovellMaker.Core/Audio/PcmWave.cs), [tickets](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.Audio.cs). Тесты: [ScriptAudioLuaTests](../../tests/NovellMaker.Core.Tests/Screens/ScriptAudioLuaTests.cs).

<a id="storage"></a>
## Слоты и профиль

| Область | Предел |
| --- | --- |
| Слоты | 128 с quickslot; до 16 МиБ/файл |
| ID слота | 1..64 ASCII a-z0-9_- без Windows device names |
| Metadata | title<=128, scene<=256, phrase<=512, revision<=128 |
| Операции slot host | одна активная; хранилище одна +32 ожидающих |
| Подготовка сохранений на процесс | 2 активные +32 ожидающих |
| Профиль v3 | <=128 КиБ; старые v1/v2 <=16 КиБ |
| Persistent Data | 256 строк, key AuthorKey<=64, value<=1024, сумма<=16384 |
| Профильные mutation-команды runtime | одна ожидающая подтверждения записи; чтения разрешены параллельно записи |
| Очередь записи профиля | одна активная +последний объединённый снимок |
| Громкость настройки | [0,1]; mute/fullscreen boolean |

Источник: [slot store](../../src/NovellMaker.Core/Execution/GameSlotStore.cs), [save](../../src/NovellMaker.Core/Execution/GameSave.cs), [profile](../../src/NovellMaker.Core/Profiles/PlayerProfileStore.cs), [Data](../../src/NovellMaker.Core/Profiles/PlayerProfileData.cs). Тесты: [слоты](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs), [профиль](../../tests/NovellMaker.Core.Tests/Profiles/PersistentDataTests.cs).

<a id="external"></a>
## Проверка выпуска

| Область | Предел |
| --- | --- |
| ReleaseCheckService | 1 активный HTTP +4 ожидающих |
| Запрос | 10 секунд с момента принятия, включая очередь |
| Повторный HTTP | >=60 секунд, Retry-After может увеличить |
| Кеш успеха | 15 минут в памяти сервиса |
| Владельцы | 64 на сервис |
| Учтённые запросы владельца | 128 за его lifetime, даже подтверждённые ID остаются |
| Резервы результатов | 128 на сервис, дополнительно общий бюджет ScreenLuaSession |
| HTTP | заголовки<=16 КиБ, распакованное тело<=256 КиБ, JSON depth<=16 |
| Release projection | <=8192 JSON байт, page_url<=512 |
| Конфигурация | Owner<=39, Repository<=100; версия 3..4 чисел 0..65535 |
| retry_after_seconds | целое >0, до int32 max |

Источник: [service](../../src/NovellMaker.Core/ExternalServices/ReleaseCheckService.cs), [конфигурация](../../src/NovellMaker.Core/ExternalServices/ReleaseCheck.cs), [HTTP](../../src/NovellMaker.Runtime/ExternalServices/GitHubReleaseProvider.cs). Тесты: [Lua](../../tests/NovellMaker.Core.Tests/Screens/ReleaseCheckLuaTests.cs).

<a id="standard"></a>
## Ограниченная библиотека

| Область | Предел |
| --- | --- |
| Один load / обработчик со всеми модулями | 65536 native единиц работы, отдельно от 20000 Lua-инструкций |
| Строка на входе/выходе | 8192 UTF-16 |
| Все строковые результаты вызова | 32768 UTF-16 |
| Таблица библиотечной функции | 1024 физических записей, включая tombstones |
| math.min/max | 1..64 чисел |
| string.rep count | integer [0,8192], включая пустую строку |
| Индексы/позиции | int32; отдельные диапазоны у функции |
| string.find work | 1+len(text)*max(1,len(needle)), даже при коротком фактическом поиске |

Стоимость остальных функций и nil/default — в [индивидуальных описаниях](standard-library.md). Источник: [LuaStandardLibrary](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs). Тесты: [LuaStandardLibraryTests](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs).

<a id="diagnostics"></a>
## Диагностика

| Область | Предел |
| --- | --- |
| ui.log одно message | 1024 UTF-16, только info/warning/error |
| Один load / обработчик вместе с модулями | 32 записи, 8192 символов сообщений; лишние корректные отбрасываются |
| Кольцо host | 256 записей, старые вытесняются |
| Короткие diagnostics/issues сеанса | по 32, сообщение<=4096 |
| Поле Line | 1..32769, если определено |

Источник: [journal](../../src/NovellMaker.Core/Scripting/LuaDiagnosticJournal.cs), [buffer](../../src/NovellMaker.Core/Scripting/LuaLogBuffer.cs). Тесты: [LuaDiagnosticsTests](../../tests/NovellMaker.Core.Tests/Screens/LuaDiagnosticsTests.cs).
