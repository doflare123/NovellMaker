# Проверка полноты и границы достоверности

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [Примеры](examples.md)

Справочник составлен по рабочему дереву репозитория **31 октября 2026 года**, а не по одному старому отчёту или плану. Исходные незавершённые правки пользователя в C#/тестах сохранены. Production API, форматы и поведение движка при документировании не менялись. Сведения ниже относятся к этому запуску; исторические результаты удалённых этапных отчётов не выданы за новую проверку.

## Инвентаризация

Автосохранение редактора дополнено отдельными восстановительными снимками и предложением восстановления при запуске. [EditorRecoveryTests](../../tests/NovellMaker.Editor.Tests/EditorRecoveryTests.cs) проверяют незавершённые черновики, целостность основного проекта, конфликты с внешними правками, блокировку снимков работающего экземпляра, ошибки записи и принудительное завершение отдельного процесса с настоящим окном редактора в Avalonia Headless. Нативный захват окна Windows в этой проверке завершился тайм-аутом; визуальная проверка нативного окна не подтверждена. Поведение и ограничения описаны в [руководстве](engine-guide.md#автосохранение-и-восстановление-редактора).

| Контекст | Число имён | Откуда установлены |
| --- | --- | --- |
| game.* | 24 | Bind в LuaActionScript.RunInWorker |
| ui.* | 50 | ScreenLuaVm, LuaAnimationApi, LuaAudioApi, LuaRandomApi и ui.import из bootstrap |
| Глобальные type/tostring/tonumber/pairs/ipairs | 5 | LuaStandardLibrary.Install, только ui и модули |
| math.* функции | 20 | явные Unary/Binary/Bind и min/max loop |
| string.* функции | 7 | Bind и lower/upper/reverse loop |
| table.* функции | 3 | Bind |
| **Всего зарегистрированных публичных имён функций** | **109** | собственные описание и ссылка в указателе для каждого |

Модули не добавляют новых встроенных экспортов: у них те же 50 ui-функций и 35 библиотечных, со своим окружением и контекстом активного обработчика. Их авторские caption/draw/next и т.п. в 109 не включены. `on_load`/`on_event` — пользовательские точки входа, не зарегистрированные функции вызова; они описаны в модели/событиях. Внутренние bootstrap resolve/publish, C# методы worker/host и функции конкретных примеров исключены. refs/modules/audio/ui.null/math.pi — данные, описаны отдельно.

Проверены все места `CoreModules`, `Globals.Set`, `NewCallback` в Core. Сценарный и событийный конструкторы используют `CoreModules.None`; syntax-VM API вообще не исполняет. Публичные таблицы устанавливаются только разобранными регистраторами. Итераторы pairs/ipairs возвращают внутренний callback как результат и описаны у соответствующей функции, не как ещё два именованных API.

## Воспроизводимая статическая проверка

```powershell
python scripts/check-lua-reference.py
```

[Скрипт проверки](../../scripts/check-lua-reference.py) извлекает имена из текущих регистраций, включая имя-bearing loops и импорт bootstrap, независимо от Markdown/LuaCATS. Проверяет:

- полное совпадение регистраций с алфавитным указателем и отсутствие дубликатов;
- отдельный раздел каждой функции, сигнатуру/пример, доступность, аргументы, возврат, эффект, ссылки на код и тесты;
- наличие описания каждого из 21 kind событий, извлечённых из ValidateEvent;
- совпадение имён с game.lua/ui.lua/library.lua LuaCATS;
- существование относительных файловых ссылок и anchors в новом справочнике и корневом README.

Это проверка структуры/покрытия имён, **не доказательство смысла каждого предложения**. Аргументы/default/nil, перечисления, квоты, протокол и host-применение сверялись чтением кода и существующих тестов. При изменении формы регистратора скрипт нужно адаптировать; нельзя автоматически считать старый extractor достаточным для новой системы API.

## Сверка по цепочке VM → протокол → host → тест

| Категория | Регистрация и аргументы | Проверка/применение | Основные тесты |
| --- | --- | --- | --- |
| game | [LuaActionScript](../../src/NovellMaker.Core/Execution/LuaActionScript.cs) | [каталог](../../src/NovellMaker.Core/Execution/LuaInvocationSnapshot.cs), [валидатор](../../src/NovellMaker.Core/Execution/ScenarioProgram.cs), [player](../../src/NovellMaker.Core/Execution/ScenarioPlayer.cs), [pending](../../src/NovellMaker.Core/Execution/ScenarioPlayer.Lua.cs) | [ScenarioPlayerTests](../../tests/NovellMaker.Core.Tests/Execution/ScenarioPlayerTests.cs), [PendingLuaTests](../../tests/NovellMaker.Core.Tests/Execution/PendingLuaTests.cs) |
| ui дерево/input/state | [ScreenLuaWorker](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs) | [protocol](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs), [session](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.cs), [stack/input](../../src/NovellMaker.Core/Screens/ScreenStack.Input.cs) | [WorkerTests](../../tests/NovellMaker.Core.Tests/Screens/ScreenLuaWorkerTests.cs), [TreeTests](../../tests/NovellMaker.Core.Tests/Screens/ScreenTreeTests.cs), [ComponentStateTests](../../tests/NovellMaker.Core.Tests/Screens/ComponentStateTests.cs), [ExtendedInputTests](../../tests/NovellMaker.Core.Tests/Screens/ExtendedInputTests.cs) |
| Анимации/звук | [Animation API](../../src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs), [Audio API](../../src/NovellMaker.Core/Screens/Lua/LuaAudioApi.cs) | [аниматор](../../src/NovellMaker.Core/Screens/Animations/ScreenAnimation.cs), [audio host](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.Audio.cs), [AudioService](../../src/NovellMaker.Core/Audio/AudioService.cs) | [AnimationTests](../../tests/NovellMaker.Core.Tests/Screens/AnimationTests.cs), [ScriptAudioLuaTests](../../tests/NovellMaker.Core.Tests/Screens/ScriptAudioLuaTests.cs) |
| Сервисы/профиль/слоты | [Worker](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [command/result](../../src/NovellMaker.Core/Execution/GameServiceCommand.cs) | [RuntimeGameServices](../../src/NovellMaker.Runtime/RuntimeGameServices.cs), [RuntimeSettingsServices](../../src/NovellMaker.Runtime/RuntimeSettingsServices.cs), [SlotStore](../../src/NovellMaker.Core/Execution/GameSlotStore.cs) | [GameServiceLuaTests](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs), [GameSlotTests](../../tests/NovellMaker.Core.Tests/Execution/GameSlotTests.cs), [RuntimeSettingsServicesTests](../../tests/NovellMaker.Runtime.Tests/RuntimeSettingsServicesTests.cs) |
| Мини-игры | [Worker](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [schema](../../src/NovellMaker.Core/Minigames/MinigameDefinition.cs) | [ScenarioPlayer.Minigames](../../src/NovellMaker.Core/Execution/ScenarioPlayer.Minigames.cs), [RuntimeScreens.Minigames](../../src/NovellMaker.Runtime/RuntimeScreens.Minigames.cs) | [MinigameLuaTests](../../tests/NovellMaker.Core.Tests/Screens/MinigameLuaTests.cs), [MinigameTests](../../tests/NovellMaker.Core.Tests/Execution/MinigameTests.cs) |
| Модули/библиотека/RNG | [loader](../../src/NovellMaker.Core/Scripting/LuaModuleLoader.cs), [library](../../src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs), [RNG API](../../src/NovellMaker.Core/Scripting/LuaRandomApi.cs) | [graph](../../src/NovellMaker.Core/Scripting/LuaModuleLibrary.cs), [RNG state](../../src/NovellMaker.Core/Scripting/LuaRandomState.cs) | [ModuleTests](../../tests/NovellMaker.Core.Tests/Screens/LuaModuleTests.cs), [LibraryTests](../../tests/NovellMaker.Core.Tests/Screens/LuaStandardLibraryTests.cs), [RandomTests](../../tests/NovellMaker.Core.Tests/Screens/LuaRandomTests.cs) |
| Проверка выпуска | [Worker](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [codes](../../src/NovellMaker.Core/ExternalServices/ReleaseCheck.cs) | [host bridge](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaSession.ReleaseChecks.cs), [service](../../src/NovellMaker.Core/ExternalServices/ReleaseCheckService.cs), [HTTP](../../src/NovellMaker.Runtime/ExternalServices/GitHubReleaseProvider.cs) | [ReleaseCheckLuaTests](../../tests/NovellMaker.Core.Tests/Screens/ReleaseCheckLuaTests.cs), [ReleaseCheckProviderTests](../../tests/NovellMaker.Runtime.Tests/ReleaseCheckProviderTests.cs) |

## LuaCATS и прежняя документация: расхождения

Все **109 имён** есть в текущих LuaCATS, отсутствующих/устаревших имён функций не найдено. При этом полноты имён недостаточно для справочника:

1. В [ui.lua](../../src/NovellMaker.Editor/LuaApi/ui.lua) `NMEvent.operation/status/code` и `NMSlot.status` описаны широко либо неполно: нет полного набора operation, ReleaseCheckCode, комбинаций animation status/reason и отсутствия CancelReleaseCheck callback. В [events](events.md) перечислены фактические значения производителя/валидатора отдельно.
2. Комментарий `ui.get_state` «host-committed» не поясняет read-after-set в **том же** обработчике: Worker сразу присваивает рабочий state. Справочник различает подготовленные данные VM и подтверждённые host. [Источник Handle/set_state](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [ComponentStateTests](../../tests/NovellMaker.Core.Tests/Screens/ComponentStateTests.cs).
3. Optional-метки LuaCATS у библиотечных log/sub/rep/find/concat и tween easing не объясняют, что отсутствие выбирает default, а **явный nil отвергается**. Это проверено `args.Count` и exact-type чтением; у game.wait_click тоже есть особое отличие nil от отсутствия. У service new_game/save_slot/persistent nil имеет иной смысл.
4. Аннотация cancel_animation говорит о своём ID, но host не ругается на неизвестный корректный GUID — это no-op. Для аудио и release cancel правила строже. Они не унифицированы в документации вопреки коду.
5. В старых этапных страницах оставались v8/v10/v18, прежние wire-версии и фразы «сервисы/модули/мини-игры/анимации ещё не реализованы». Они заменены фактическими ограничениями текущего v20/wire18. Старые отчёты больше не служат инструкцией автору.
6. LuaCATS объединяет среды ради подсказок и не может само запретить game в ui или ui в game. В новом справочнике это явное правило каждой записи; библиотека и ui.import не приписаны сценарным ресурсам.

LuaCATS при этой работе не подменялся новой runtime-регистрацией. Его ограничения зафиксированы здесь; выполнять meta-файлы в игре нельзя.

## Особенности и неоднозначности кода, оставленные без исправления

- **game.finish не обновляет текущий game.get.** Он создаёт SetAction напрямую, тогда как game.set дополнительно меняет локальный variables builder. `game.finish("new"); game.get("minigame.result")` в том же вызове видит старый снимок. Это несогласованность старого API, не lifecycle-команда. [Строки регистрации](../../src/NovellMaker.Core/Execution/LuaActionScript.cs), поведенческий тест `LegacyFinishDoesNotChangeTheCurrentLuaGetSnapshot` в [новых тестах](../../tests/NovellMaker.Core.Tests/Screens/LuaReferenceExamplesTests.cs).
- **game.minigame_result при cancelled/failed возвращает data не nil, а null userdata**, но в сценарном окружении нет ui.null/type. Практический безопасный контракт — сначала status, обращаться к полям data только при completed с ожидаемой схемой. Проверено `CancelledMinigameDataIsANullMarkerRatherThanLuaNilInScenarioWorker`.
- **hover принимается протоколом, но текущий runtime его не генерирует.** Нельзя строить логику на ожидании этого kind; используйте hover_enter/leave. [ValidateEvent](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs), [маршрутизатор](../../src/NovellMaker.Core/Screens/ScreenStack.Input.cs).
- **Периодический update доступен мини-игре**, а наличие kind в общей таблице протокола само не означает таймер для обычного экрана. Источник вызова — [TickMinigame](../../src/NovellMaker.Runtime/RuntimeScreens.Minigames.cs).
- **is_restoring остаётся true у всего восстановленного экземпляра.** Проверки «ничего не делать при restoring» во всех on_event остановят логику после Load; ограничение касается восстанавливающего on_load. Источник — неизменяемый MinigameInvocation в [worker](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs).
- **Старые bindings принимают лишние аргументы/часть преобразований AsType**, новые audio/RNG/animation/standard требуют точного count/type. Документация не обещает единый строгий стиль, которого нет в коде. Для предсказуемого кода используйте показанные сигнатуры и точные типы.
- **Закрывающие успешные сервисы не присылают callback в старую VM.** Это осознанная граница lifetime, а не потерянный синхронный результат. [RuntimeGameServices](../../src/NovellMaker.Runtime/RuntimeGameServices.cs).

Ни один из этих пунктов не исправлен скрыто в движке ради соответствия тексту. Для изменения поведения нужна отдельная задача с решением о совместимости.

## Выполненные тесты

Среда: Windows x64, .NET SDK 10.0.204. Выполнен `dotnet restore NovellMaker.slnx --locked-mode`, затем Release-тесты всей solution. Первый no-restore запуск в sandbox не нашёл MoonSharp в кэше пользователя sandbox; после штатного locked restore в разрешённом окружении тесты прошли. Это сбой окружения до тестов, не дефект Lua API.

Итоговый полный прогон после добавления 14 проверок справочника:

| Набор | Пройдено | Ошибок | Пропущено |
| --- | --- | --- | --- |
| Core | 1633 | 0 | 7 |
| Runtime | 192 | 0 | 10 |
| Editor | 273 | 0 | 60 |
| Всего | **2098** | **0** | **77** |

Новые [LuaReferenceExamplesTests](../../tests/NovellMaker.Core.Tests/Screens/LuaReferenceExamplesTests.cs) читают именованные исходники прямо из examples.md:

| Пример/проверка | Что проверяется |
| --- | --- |
| scenario-queue | настоящий сценарный worker и host-барьер: started → таймер → done/input |
| counter | настоящий ui-worker, click, state+текст, новый экземпляр снова 0 |
| module-helper/root | настоящий worker, объявленный граф, вызов функции импорта → «Счёт: 2» |
| random-checkpoint | настоящий worker, два одинаковых случайных значения после restore потока |
| minigame-handler | настоящий worker, state/schema, повторное Attach с restoring/state/RNG, два дальнейших click → один completed |
| minigame-launch/after | настоящий сценарный worker, барьер мини-игры и новый ресурс с committed результатом; host завершение подано тестом |
| animation | настоящий worker + host timeline → x=200/opacity=0.5 и коррелированный completed:finished |
| profile | настоящий worker + проверенная сервисная очередь; ответы чтения/записи поданы тестом, дисковая часть отдельно покрыта существующими RuntimeSettingsServices/Profile tests |
| release | настоящий worker и ReleaseCheckService с Disabled; отсутствие HTTP подтверждено provider, который не должен вызываться |
| audio | реальная worker-VM и protocol Prepare: owned play и коррелированный audio_result; backend/нативное звучание новым тестом не проверялись |
| отсутствующие globals | реальная component VM: 30 важных глобальных возможностей отсутствуют |
| finish/nil/minigame null | настоящий сценарный worker подтверждает три существенных исключения; отдельно проверено старое AsType-приведение numeric string/number |

Это **поведенческие проверки**, кроме отдельно отмеченных границ backend/диска/сети. Syntax-only проверка не названа проверкой поведения. Короткие фрагменты отдельных карточек функций дополнительно сверены с их регистрацией и существующими тестами; они не все исполнены как самостоятельные полные проекты.

Все 14 новых тестов прошли также отдельным целевым запуском. Итоговая Release-сборка не дала предупреждений компилятора; `git diff --check` не нашёл ошибок пробелов. Проверка `check-lua-reference.py` завершилась без пропущенных/лишних имён, без отсутствующих описаний и без сломанных ссылок. Локальные TRX находятся в `artifacts/lua-reference-tests` (префикс `lua-reference-final`, отдельный `lua-reference-examples.trx`), это генерируемые артефакты, не часть авторского справочника.

После проверки удалены **108** прежних страниц и этапных отчётов из корня docs. В docs остался только этот справочник из **19** связанных страниц; README стал короткой точкой входа. Историческая хроника в AGENTS заменена ссылками на справочник, архитектурные требования и направление продукта сохранены. Служебные README проекта-примера и устанавливаемых LuaCATS остаются инструкциями своих артефактов, а не альтернативными API-справочниками.

## Что не проверялось заново

Нативные MonoGame/UI/export/installer smoke без флагов пропускаются; live GitHub-запрос не включался. Пропуски также включают необязательные измерения, проверки ссылок без нужных прав и Unix-имя на Windows. Их нельзя суммировать как успех. Новое тестирование не подтверждает нативную работу Linux/macOS, чистую VM без SDK, реальное звучание примера на оборудовании или отображение каждого пикселя GUI. Отдельная полная ОС-квота worker на Linux/macOS по коду отсутствует. Эти пределы поддержки отражены в [модели](execution-model.md) и [руководстве](engine-guide.md).

Синтаксический API-аудит/ссылки/109 разделов и перечисленные тесты закрывают документирование текущего публичного Lua API. Указанные особенности кода и платформенные проверки остаются открытыми вопросами, не скрытыми обещаниями функционала.
