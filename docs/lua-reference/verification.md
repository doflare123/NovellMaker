# Проверка полноты и границы достоверности

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [Примеры](examples.md)

## Повторный приём озвучки и лист по фильтру — 9 ноября 2026

В приёме листа WAV сравниваются по SHA-256 с текущим голосовым ресурсом **ID фразы + язык**. Неизменившиеся записи показаны отдельно и не копируются; кнопка считает только реальные назначения/замены. При полном совпадении не меняются каталог, ID, dirty-флаг и Undo/redo. В смешанном пакете изменение уже проверенного WAV отклоняет пакет без частичных привязок и копий. Форматы проекта v25, сохранений и Lua API не менялись.

[RecordingAuthoringTests](../../tests/NovellMaker.Editor.Tests/RecordingAuthoringTests.cs) проверяет повторную отправку 30 WAV с одним изменённым дублем того же размера/длительности: 29 записей без изменений, одна замена, 31 ресурс с сохранённым старым дублем для Undo. Undo/redo восстанавливает все привязки одним шагом, повторный приём не накапливает копии, а экспорт через WindowsExportService с тестовым шаблоном runtime содержит 31 WAV. Проверены отказ смешанного пакета при изменении пропускаемого файла после предпросмотра и удаление незавершённых копий. Старые ресурсы, нужные для Undo или других ссылок, автоматически не удаляются.

Выгрузка листа предлагает «Вся сцена / Строки по фильтру». CSV и TSV проверены на сочетании фильтров персонажа и отсутствующей озвучки: выгружаются только нужные реплики, исходные номера, ID и язык сохраняются. Полный вариант включает контекст и явно неозвученные фразы. Пустой фильтр, отмена выбора и отмена файла не перезаписывают предыдущий лист и не меняют проект. GUI в [EditorWindowTests](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) проверяет передачу фильтра кнопкой «Лист…», выбор области/пустой фильтр/отмену и счётчик реальных замен в предпросмотре. PNG: `artifacts/screenshots/recording-reimport`. Изменений runtime-аудиотракта в этой правке нет; повторный нативный прогон F6/экспорта с запуском игры не выполнялся.

Локальный прогон всех Editor-тестов: **339** пройдено, ошибок **0**, пропущено **72** необязательных native/live/benchmark/платформенных случая. TRX: `recording-reimport`. Проверка `python scripts/check-lua-reference.py` проходит. Core и runtime не менялись; полный прогон решения и выпускной Delivery для этой правки не запускались.

## Настраиваемый импорт JSON/CSV/TSV — 6 ноября 2026

В GUI доступен единый мастер импорта с настройкой разделителя/колонок, сохранением профилей, явным сопоставлением персонажей, выбором текущей/новой сцены, предпросмотром и необязательными WAV/изображениями. Формат проекта **v25** сохраняет внешние ключи и профили; пустая секция `imports` не записывается. Миграции v1–v24 дают пустые данные, непустое новое поле под старой версией отклоняется. Версии GameSave v4, screen protocol v20 и Lua API не меняются. [Контракт и примеры](dialogue-import.md).

[DialogueImportTests](../../tests/NovellMaker.Editor.Tests/DialogueImportTests.cs) проверяет 30 реплик с произвольными колонками CSV, объединение языков, обновление по ключам после перестановки с сохранением ID/TextKey/ScriptBlocks, отсутствие дубликатов неизменившихся ресурсов, JSON со сценами/позами/переводами/голосами, импорт только персонажей и отключение WAV, единую отмену/повтор, Save As/повторное открытие, миграцию/валидацию метаданных и очистку ключей удалённых объектов. Ошибка или изменение байтов при копировании не оставляет частичного проекта/ресурсных копий. GUI в [EditorWindowTests](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) проверяет выбор колонок/персонажа, сохранение и загрузку профиля, предпросмотр отсутствующего WAV, режим без звука, сброс устаревшего предпросмотра и отмену без изменений. PNG: `artifacts/screenshots/dialogue-import`.

Нативный прогон Windows **2/2**, без ошибок и пропусков: 30 реплик двух импортированных персонажей со статическими позами и PCM WAV после перестановки запускаются в runtime F6 и self-contained Windows-экспорте. Проверены реальное изображение персонажа, ID до/после перехода, продвижение голосового курсора и отсутствие runtime-диагностик. Экспорт проверяется с отключённым SDK в PATH/DOTNET_ROOT. Evidence: `artifacts/screenshots/dialogue-import/preview`, `artifacts/screenshots/dialogue-import/export`, TRX `dialogue-import-native`. Использованы синтетические изображения и WAV; актёрский рабочий процесс и нативные GUI/аудио Linux/macOS этим прогоном не подтверждены. Общий таймлайн и импорт Lua/выборов/произвольных потоков не добавлялись.

Итоговый локальный Debug-прогон: **2343** пройдено (Core 1815, Runtime 196, Editor 332), ошибок **0**, пропущено **90** необязательных native/live/benchmark/платформенных случаев. Два нативных случая этого импорта выполнены отдельно без пропусков на итоговом Windows-шаблоне v25. TRX: `dialogue-import-final` и `dialogue-import-native-final`. Проверка ссылок и покрытия справочника проходит. Это проверка рабочей копии на Windows с установленным SDK; полный выпускной Delivery и удалённые Windows/Linux/macOS CI в этой задаче не запускались.

## Реплики и пакетная озвучка — 5 ноября 2026

Проект **v24** добавляет `audio.voiceOmissions` — набор языков намеренно неозвученной фразы по её ID. При открытии v23 без этого поля используется пустой набор; запись и отметка одного языка одновременно недопустимы. Копирование фразы/сцены, удаление, undo/redo и сохранение сохраняют или очищают эти связи вместе с существующей озвучкой. GameSave v4 и screen protocol v20 не меняются. Старые runtime-шаблоны отклоняются проверкой версии; локальный Windows-шаблон подготовлен заново.

[RecordingAuthoringTests](../../tests/NovellMaker.Editor.Tests/RecordingAuthoringTests.cs) проверяет вставку 30 реплик двух персонажей, CSV/TSV с кириллицей, кавычками, табуляцией и многострочным текстом, экспорт листа, перестановку фраз, импорт по ID/языку, один undo/redo всего пакета, сохранение/повторное открытие и снимок F6. Отдельно покрыты локализованные отметки «без озвучки», миграция, конфликт привязок, отсутствующие/повреждённые WAV, неизвестные ID, замены и отказ всего пакета при изменении WAV после предпросмотра с удалением скопированных файлов. GUI в [EditorWindowTests](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) проверяет ввод в строках, Ctrl+Enter и фокус новой фразы, сопоставление неизвестного имени сразу для всей вставки, фильтр, реальный file-drop, выбранное имя дубля, undo/redo и отмену предпросмотра пакета. PNG проверены в размерах 1280 и 1600 по ширине.

Нативная проверка **2/2** на Windows: те же 30 WAV после перестановки воспроизводятся потоковым бэкендом редактора и в настоящем runtime F6, затем в self-contained Windows-экспорте с отсутствующим SDK в PATH/DOTNET_ROOT. Позиция голоса растёт у правильных ID до и после перехода; runtime-диагностик нет. Артефакты: `artifacts/screenshots/dialogue-recording` и TRX `dialogue-recording-native`. Использованы синтетические PCM WAV, а не актёрская запись; субъективное удобство работы на настоящей сцене и нативный GUI/аудиотракт Linux/macOS этим прогоном не подтверждены. Общий таймлайн не добавлялся.

Локальный регрессионный прогон Debug: **2331** пройдено (Core 1815, Runtime 196, Editor 320), ошибок **0**, пропущено **88** необязательных native/live/benchmark/платформенных случаев. Нативные два случая этой возможности выполнены отдельно без пропусков. После уточнения обработки повреждённых WAV дополнительно выполнены целевые тесты таблицы/вставки/листов записи. Проверка `python scripts/check-lua-reference.py` проходит. Это проверка рабочей копии на Windows с SDK, не полный выпускной Delivery и не ручная оценка актёрской сцены.

## Расширенный Delivery и нативный прогон переносимости — 5 ноября 2026

Обязательный [набор Delivery](../../scripts/verify-release-results.ps1) расширен с 3 до **11 случаев**: два базовых runtime, установщик, два чтения/истории, четыре слоистой/покадровой анимации с восстановлением и два управления голосом. [ReadingNativeTests](../../tests/NovellMaker.Editor.Tests/ReadingNativeTests.cs), [CharacterAnimationTests](../../tests/NovellMaker.Editor.Tests/CharacterAnimationTests.cs) и [ScriptAudioNativeTests](../../tests/NovellMaker.Editor.Tests/ScriptAudioNativeTests.cs) берут предпросмотр и экспортный шаблон из распакованной проверяемой поставки, отключают SDK в PATH/DOTNET_ROOT дочернего runtime и сохраняют evidence рядом с TRX. Проверка допуска отвергает отсутствие, пропуск, ошибку и дублирование каждого нового случая; регрессии входят в обычный CI.

[Нативный прогон переносимости](../../scripts/check-native-portability.ps1) публикует self-contained runtime для архитектуры текущей ОС и требует **6 случаев** без пропусков: два рисования/переходов/аудио, два паузы/возврата авторского UI через screen Lua worker, выполнение game Lua с отказом всего пакета по бюджету инструкций и повторным успешным вызовом, отмена с завершением настоящего дочернего worker. Публикация имеет отдельные RID restore/lock-файлы в obj и не меняет версионируемые lock-файлы. Процессы runtime и отдельная проверка game worker запускаются с отсутствующим DOTNET_ROOT и сокращённым PATH. [CI](../../.github/workflows/ci.yml) добавляет Linux под Xvfb/software GL и macOS; release зависит от этих заданий через вызываемый workflow. TRX, PNG/JSON и `portability.json` сохраняются отдельно в `artifacts/native-portability/<rid>/<run>`. Linux использует OpenAL null backend: это проверка нативного аудиотракта, а не слышимого звука на оборудовании.

Этот прогон проверяет публикацию runtime, **не экспорт новеллы или установщик Linux/macOS**. В `portability.json` явно указаны `exportQualified=false` и текущая политика памяти. Полной ОС-квоты памяти worker на Linux/macOS по-прежнему нет: GC heap 64 МиБ и таймаут не эквивалентны Windows Job Object 256 МиБ. Переносимость остаётся незавершённой до нативных результатов целевых ОС, реализации/проверки их экспорта и отдельного решения общей квоты памяти. Добавление задания CI само по себе не подтверждает его успешный запуск.

Локально на Windows x64 расширенный Delivery прошёл **11/11**, ошибок и пропусков **0**; ZIP, `qualification.json`, TRX и evidence: `artifacts/release/cafb47d727ac4dd9a2605be730baabc6`. Проверены свежая распакованная поставка, окно редактора, новые функции в предпросмотре/экспорте и полный цикл установщика. `worktreeDirty=true` честно фиксирует проверку незакоммиченных изменений. Новый набор NativePortability прошёл **6/6**, ошибок и пропусков **0**; evidence: `artifacts/native-portability/win-x64/15698119c79b4f2baf8ce8af7152895d`. Проверялись нативные зависимости и self-contained публикация на Windows; Linux/macOS в этой работе не запускались. Restore использовал локальный кэш и `NuGetAudit=false` из-за ограниченного сетевого окружения; в CI аудит не отключён.

## Авторские окна чтения — 3 ноября 2026

Проект v23 и screen wire v20 заменяют runtime-конструктор истории/параметров чтения обычными сценами и Lua-ресурсами проекта. [HistoryLuaTests](../../tests/NovellMaker.Core.Tests/Screens/HistoryLuaTests.cs) проверяет аргументы/атомарный сервисный пакет, Unicode-страницы и пустую историю, ответ через настоящий worker, детерминированную миграцию v22, сохранение авторской геометрии/перевода, повторное добавление шаблона без дублирования скрипта и отказ неверных ссылок v23. [RuntimeHistoryServicesTests](../../tests/NovellMaker.Runtime.Tests/RuntimeHistoryServicesTests.cs) проверяет привязку к посещению, отсутствие сценарных эффектов, паузу/закрытие/смену поколения и открытие заданной автором сцены. [ScreenTextureCacheTests](../../tests/NovellMaker.Runtime.Tests/ScreenTextureCacheTests.cs) проверяет полосы Text с общими бюджетами. [GUI](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) проверяет создание через инспектор, подписи/координаты, цели обоих действий и undo/redo.

На Windows x64 прошли **оба** варианта [ReadingNativeTests](../../tests/NovellMaker.Editor.Tests/ReadingNativeTests.cs): MonoGame-предпросмотр и self-contained экспорт из актуального шаблона. Проверены авторские английские подписи/координаты из `.novell`, реальные нажатия переключения ожидания голоса, повтор озвучки, возврат старого сохранения и перелистывание длинного текста. PNG экспорта просмотрен: текст/переносы/кнопки находятся в заданных областях. Строки Unicode сохранены в данных; наличие глифов определяется существующим шрифтом Text-компонентов — в этой среде CJK/emoji отображаются отсутствующими глифами. Этот патч не добавляет выбор шрифта или font fallback для обычного Text.

Полный [контракт и ограничения](history.md): одно посещение/страница на запрос, ограниченные метаданные, повтор только текущего visit_id; шаблон не является прокручиваемым списком всех записей. Размер страницы согласуется с авторской высотой/шириной. Переход на другую точную ревизию проекта сохраняет существующие правила несовместимости игровых слотов. Linux/macOS в этой работе нативно не проверялись.

Справочник составлен по рабочему дереву репозитория **31 октября 2026 года**, а не по одному старому отчёту или плану. Исходные незавершённые правки пользователя в C#/тестах сохранены. Исходный API-аудит не менял поведение движка; последующее завершение базового чтения описано отдельной проверкой ниже. Сведения ниже относятся к этому запуску; исторические результаты удалённых этапных отчётов не выданы за новую проверку.

## Выпускной допуск Windows x64

[Release delivery gate](../../.github/workflows/release.yml) отделён от обычного CI. Он требует успешной матрицы Windows/Linux/macOS и нативного Windows Delivery перед созданием черновика выпуска. [Скрипт допуска](../../scripts/check-release.ps1) собирает автономный ZIP редактора с runtime-шаблоном и NSIS, распаковывает его и проверяет именно распакованную поставку. [Проверка TRX](../../scripts/verify-release-results.ps1) отклоняет отсутствующие отчёты, пустые наборы, пропуски/inconclusive, ошибки и неполные/дублированные обязательные случаи. [Регрессии допуска](../../tests/ReleaseGate.Tests.ps1) проверяют положительный случай и восемь отказов и входят в обычный CI.

[WindowsInstallerTests](../../tests/NovellMaker.Editor.Tests/WindowsInstallerTests.cs) создаёт отдельный проект через команду редактора «Пример», сохраняет/открывает его, запускает настоящий предпросмотр из поставки, экспортирует/устанавливает игру, проверяет запуск без SDK в PATH/DOTNET_ROOT, сохранение и загрузку, удаление с сохранением всех пользовательских файлов и повторную установку с загрузкой прежнего слота новым процессом. Выпускной режим дополнительно запускает окно поставленного редактора. Диалоги создания/сохранения подменены в тесте; это не автоматизация кликов по всему интерфейсу. [NativeRuntimeSmokeTests](../../tests/NovellMaker.Runtime.Tests/NativeRuntimeSmokeTests.cs) в Delivery выполняет два случая рисования/переходов с runtime из этой же поставки.

Иконка и версия игрового EXE теперь встраиваются после проверки шаблона, в staging-копию apphost. [WindowsExportTests](../../tests/NovellMaker.Editor.Tests/WindowsExportTests.cs) проверяют название/версию через FileVersionInfo, точные байты ICO-ресурсов, повторное оформление, неизменность шаблона и отказ при испорченной ICO. Нативный тест установщика повторяет проверку ресурсов уже установленного EXE.

Локальный полный Release-прогон: **2174** пройдено (Core 1676, Runtime 192, Editor 306), ошибок **0**, пропущено **85** (необязательные native/live/benchmark/платформенные случаи). Нативный Delivery прошёл отдельно: **3/3**, ошибок и пропусков **0**, включая запуск окна редактора из распакованного ZIP. Артефакты последнего прогона: `artifacts/release/2223402c8efb4c7ab0018c49722966ab` (ZIP, qualification.json, TRX и PNG/JSON evidence). В локальном qualification честно указан worktreeDirty=true: проверялись текущие незакоммиченные изменения. Обычный прогон с пропусками не выдаётся за выпускной допуск. Проверка ссылок/покрытия справочника проходит. Запуски выполнялись на Windows с установленным SDK; автономность проверяется отключением DOTNET_ROOT/PATH в дочерних процессах, это не чистая VM. Локальный restore использовал кэш и NuGetAudit=false из-за недоступности NuGet audit endpoint; в CI отключение аудита не добавлено. GitHub Actions на удалённых Windows/Linux/macOS в рамках этой задачи не запускался.

## Проверка базового чтения

В рамках задачи чтения добавлены [ReadingPlayback](../../src/NovellMaker.Core/Execution/ReadingPlayback.cs), история (введена в GameSave v3, сохраняется текущим v4), профиль v4 с отдельными отметками прочитанного, четыре действия холста в проекте v21 и поля настроек screen protocol v19. Пользовательские правила приведены в [руководстве](engine-guide.md#чтение-новеллы).

Полный Debug-прогон `dotnet test NovellMaker.slnx --no-restore -p:UsedAvaloniaProducts=`: Core **1653**, Runtime **192**, Editor **295** пройдено; всего **2140**, ошибок **0**, пропущено **79**. После заключительных исправлений отдельно пройдены **75** проверок чтения/Unicode/settings-worker и повторно **192** Runtime-теста. Пустое UsedAvaloniaProducts отключает задачу build-телеметрии, которая в sandbox не могла записать лог в профиль пользователя; это не изменение runtime.

[ReadingPlaybackTests](../../tests/NovellMaker.Core.Tests/Execution/ReadingPlaybackTests.cs) проверяют Unicode и цветовые метки, ручное раскрытие, задержку и голос в авто, остановку пропуска перед новым текстом, выбор/мини-игру/интерактивное ожидание, pending Lua, загрузку без replay, сохранение отметок старого слота и границу истории. [ReadingProfileTests](../../tests/NovellMaker.Core.Tests/Profiles/ReadingProfileTests.cs) проверяют миграцию профиля v3, запись, backup, диапазоны/типы параметров. [AudioTests](../../tests/NovellMaker.Core.Tests/Audio/AudioTests.cs) подтверждают отдельный источник повтора без возобновления сценарного звука. [GameServiceLuaTests](../../tests/NovellMaker.Core.Tests/Screens/GameServiceLuaTests.cs) передают новые настройки через настоящий worker.

Обе проверки [ReadingNativeTests](../../tests/NovellMaker.Editor.Tests/ReadingNativeTests.cs) с включёнными NOVELLMAKER_NATIVE_SMOKE/NOVELLMAKER_EXPORT_SMOKE прошли: окно предпросмотра и экспортированная self-contained Windows x64 игра. Проверяется реально проецируемый частичный текст, второй ввод, отдельное открытие источника голоса, отсутствие движения сценарного времени при истории, авточтение, Load со старой историей и сохранённым профилем, пропуск с остановкой на третьей новой фразе. При запуске экспорта DOTNET_ROOT/PATH не указывают SDK. Снимки истории и настроек чтения проверены визуально, включая отсутствие перекрытия кнопки закрытия навигацией. Два прежних нативных теста переходов Next/Previous и цветного текста тоже прошли; их профиль явно задаёт мгновенный текст, отдельные ReadingNativeTests проверяют постепенный. Это не проверка услышанного звука человеком и не чистая VM без SDK. Артефакты: `artifacts/screenshots/reading`, TRX: `tests/*/TestResults/reading-*.trx`.

## Инвентаризация

Автосохранение редактора дополнено отдельными восстановительными снимками и предложением восстановления при запуске. [EditorRecoveryTests](../../tests/NovellMaker.Editor.Tests/EditorRecoveryTests.cs) проверяют незавершённые черновики, целостность основного проекта, конфликты с внешними правками, блокировку снимков работающего экземпляра, ошибки записи и принудительное завершение отдельного процесса с настоящим окном редактора в Avalonia Headless. Нативный захват окна Windows в этой проверке завершился тайм-аутом; визуальная проверка нативного окна не подтверждена. Поведение и ограничения описаны в [руководстве](engine-guide.md#автосохранение-и-восстановление-редактора).

| Контекст | Число имён | Откуда установлены |
| --- | --- | --- |
| game.* | 25 | Bind в LuaActionScript.RunInWorker |
| ui.* | 51 | ScreenLuaVm, LuaAnimationApi, LuaAudioApi, LuaRandomApi и ui.import из bootstrap |
| Глобальные type/tostring/tonumber/pairs/ipairs | 5 | LuaStandardLibrary.Install, только ui и модули |
| math.* функции | 20 | явные Unary/Binary/Bind и min/max loop |
| string.* функции | 7 | Bind и lower/upper/reverse loop |
| table.* функции | 3 | Bind |
| **Всего зарегистрированных публичных имён функций** | **111** | собственные описание и ссылка в указателе для каждого |

Модули не добавляют новых встроенных экспортов: им доступны те же функции: 51 из ui и 35 библиотечных, со своим окружением и контекстом активного обработчика. Их авторские caption/draw/next и т.п. в 111 не включены. `on_load`/`on_event` — пользовательские точки входа, не зарегистрированные функции вызова; они описаны в модели/событиях. Внутренние bootstrap resolve/publish, C# методы worker/host и функции конкретных примеров исключены. refs/modules/audio/ui.null/math.pi — данные, описаны отдельно.

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

Все **111 имён** есть в текущих LuaCATS, отсутствующих/устаревших имён функций не найдено. При этом полноты имён недостаточно для справочника:

1. В [ui.lua](../../src/NovellMaker.Editor/LuaApi/ui.lua) `NMEvent.operation/status/code` и `NMSlot.status` описаны широко либо неполно: нет полного набора operation, ReleaseCheckCode, комбинаций animation status/reason и отсутствия CancelReleaseCheck callback. В [events](events.md) перечислены фактические значения производителя/валидатора отдельно.
2. Комментарий `ui.get_state` «host-committed» не поясняет read-after-set в **том же** обработчике: Worker сразу присваивает рабочий state. Справочник различает подготовленные данные VM и подтверждённые host. [Источник Handle/set_state](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [ComponentStateTests](../../tests/NovellMaker.Core.Tests/Screens/ComponentStateTests.cs).
3. Optional-метки LuaCATS у библиотечных log/sub/rep/find/concat и tween easing не объясняют, что отсутствие выбирает default, а **явный nil отвергается**. Это проверено `args.Count` и exact-type чтением; у game.wait_click тоже есть особое отличие nil от отсутствия. У service new_game/save_slot/persistent nil имеет иной смысл.
4. Аннотация cancel_animation говорит о своём ID, но host не ругается на неизвестный корректный GUID — это no-op. Для аудио и release cancel правила строже. Они не унифицированы в документации вопреки коду.
5. В старых этапных страницах оставались v8/v10/v18, прежние wire-версии и фразы «сервисы/модули/мини-игры/анимации ещё не реализованы». Они заменены фактическими ограничениями текущего v22/wire19. Старые отчёты больше не служат инструкцией автору.
6. LuaCATS объединяет среды ради подсказок и не может само запретить game в ui или ui в game. В новом справочнике это явное правило каждой записи; библиотека и ui.import не приписаны сценарным ресурсам.

Исходный аудит LuaCATS не менял runtime-регистрацию. При уточнении контракта 2 ноября в runtime и LuaCATS добавлены два явных переключателя строгой проверки. Его ограничения зафиксированы здесь; выполнять meta-файлы в игре нельзя.

## Особенности и неоднозначности кода, оставленные без исправления

- **hover принимается протоколом, но текущий runtime его не генерирует.** Нельзя строить логику на ожидании этого kind; используйте hover_enter/leave. [ValidateEvent](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs), [маршрутизатор](../../src/NovellMaker.Core/Screens/ScreenStack.Input.cs).
- **Периодический update доступен мини-игре**, а наличие kind в общей таблице протокола само не означает таймер для обычного экрана. Источник вызова — [TickMinigame](../../src/NovellMaker.Runtime/RuntimeScreens.Minigames.cs).
- **is_restoring остаётся true у всего восстановленного экземпляра.** Проверки «ничего не делать при restoring» во всех on_event остановят логику после Load; ограничение касается восстанавливающего on_load. Источник — неизменяемый MinigameInvocation в [worker](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs).
- **Закрывающие успешные сервисы не присылают callback в старую VM.** Это осознанная граница lifetime, а не потерянный синхронный результат. [RuntimeGameServices](../../src/NovellMaker.Runtime/RuntimeGameServices.cs).

Оставшиеся пункты выше сохраняют прежнее поведение. Три изменения контракта описаны отдельно ниже.

## Уточнение Lua-контракта — 2 ноября 2026

- `game.finish(result)` использует ту же запись переменных и квоты, что `game.set("minigame.result",result)`. В том же вычислении `game.get` сразу видит строку; host меняется только при исполнении очереди. Lifecycle мини-игры прежний.
- `game.minigame_result().data` для JSON-null верхнего уровня — Lua nil, включая cancelled/failed и completed с null. Итог без результата по-прежнему nil целиком. False, 0, пустая строка и вложенные null сохраняются; вложенные маркеры не заменяются на nil, чтобы не ломать списки.
- [game.strict_arguments](scenario.md#game-strict-arguments) и [ui.strict_arguments](components.md#ui-strict-arguments) явно включают проверку числа аргументов и точных типов. По умолчанию старые вызовы, AsType-преобразования и игнорирование лишних аргументов сохранены. В game режим начинается с false в каждом вызове; в ui общий с модулями и сохраняется между событиями до закрытия VM. После Load включение повторяется из исходника. Переключатель всегда требует ровно один boolean. Уже строгие API не ослабляются при false.

Совместимость: имена прежних функций, форматы проекта/слота, протокол, квоты и события не менялись. Скрипты, которые полагались на старый снимок после finish или на truthy null-маркер в data верхнего уровня, должны учитывать два исправленных поведения. Строгая проверка старых вызовов применяется только после явного включения.

Проверки: [LuaApiContractTests](../../tests/NovellMaker.Core.Tests/Screens/LuaApiContractTests.cs) покрывает обе политики, обязательные/необязательные позиции и nil, неконечные числа, границы переменных finish, независимые VM, сохранение режима между событиями настоящего worker, общий режим модулей/ui.import, отсутствие частичного host-commit при ошибке, сохранение значений и вложенных null. [LuaReferenceExamplesTests](../../tests/NovellMaker.Core.Tests/Screens/LuaReferenceExamplesTests.cs) обновлён: настоящий сценарный worker проверяет новый локальный снимок и nil для трёх финальных фаз. Нативные платформенные возможности этими проверками не подтверждаются.

Проверка на Windows x64/.NET SDK 10.0.204: `dotnet test NovellMaker.slnx --no-restore -p:UsedAvaloniaProducts=` — Core 1774 пройдено/7 пропущено, Runtime 192/10, Editor 307 пройдено/68 пропущено и один сбой `ComponentDepthErrorIsContainedByActualDropHandlerAndKeepsUnsavedWork` внутри `Avalonia.Headless.HeadlessUnitTestSession.DisposeAsync`. Этот GUI-тест прошёл отдельным повтором без правок GUI; исходный полный прогон не считается полностью зелёным. После финального уточнения проекции результата отдельно пройдены все 112 проверок LuaApiContractTests/LuaReferenceExamplesTests без ошибок и пропусков. Справочник: 111 функций/111 описаний, 21 событие, без расхождений LuaCATS и сломанных ссылок. Нативные smoke-флаги не включались. TRX: `tests/*/TestResults/lua-contract*.trx`, генерируемые артефакты.

## Исходный прогон справочника

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
| finish/nil/minigame null | исходно фиксировались прежние исключения; текущие проверки обновлены по контракту 2 ноября, старое AsType-приведение numeric string/number сохранено |

Это **поведенческие проверки**, кроме отдельно отмеченных границ backend/диска/сети. Syntax-only проверка не названа проверкой поведения. Короткие фрагменты отдельных карточек функций дополнительно сверены с их регистрацией и существующими тестами; они не все исполнены как самостоятельные полные проекты.

Все 14 новых тестов прошли также отдельным целевым запуском. Итоговая Release-сборка не дала предупреждений компилятора; `git diff --check` не нашёл ошибок пробелов. Проверка `check-lua-reference.py` завершилась без пропущенных/лишних имён, без отсутствующих описаний и без сломанных ссылок. Локальные TRX находятся в `artifacts/lua-reference-tests` (префикс `lua-reference-final`, отдельный `lua-reference-examples.trx`), это генерируемые артефакты, не часть авторского справочника.

После проверки удалены **108** прежних страниц и этапных отчётов из корня docs. В docs остался только этот справочник из **19** связанных страниц; README стал короткой точкой входа. Историческая хроника в AGENTS заменена ссылками на справочник, архитектурные требования и направление продукта сохранены. Служебные README проекта-примера и устанавливаемых LuaCATS остаются инструкциями своих артефактов, а не альтернативными API-справочниками.

## Границы исходного прогона справочника

Нативные MonoGame/UI/export/installer smoke без флагов пропускаются; live GitHub-запрос не включался. Пропуски также включают необязательные измерения, проверки ссылок без нужных прав и Unix-имя на Windows. Их нельзя суммировать как успех. Новое тестирование не подтверждает нативную работу Linux/macOS, чистую VM без SDK, реальное звучание примера на оборудовании или отображение каждого пикселя GUI. Отдельная полная ОС-квота worker на Linux/macOS по коду отсутствует. Эти пределы поддержки отражены в [модели](execution-model.md) и [руководстве](engine-guide.md).

Исходный синтаксический API-аудит/ссылки/109 разделов и перечисленные тесты закрывали документирование публичного Lua API на момент прогона. После уточнения контракта документированы 111 функций. Указанные особенности кода и платформенные проверки остаются открытыми вопросами, не скрытыми обещаниями функционала.


## Озвучка и микшер: проверка 31 октября 2026

[AudioPlaybackTests](../../tests/NovellMaker.Core.Tests/Audio/AudioPlaybackTests.cs) проверяет реальные значения курсора от backend (ход часов сам прогресс не меняет), ручную/общую/owner-паузу, окончание/ошибку/смену реплики/Restore, линейные fades и crossfade, сохранение старой музыки при ошибке замены, ducking нескольких voice с Master/mute и освобождением на паузе, сброс ducking при Load, сериализацию `audio.playback` и defaults прежнего v21. GUI проверен в [EditorWindowTests](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) и [SceneAuthoringTests](../../tests/NovellMaker.Editor.Tests/SceneAuthoringTests.cs): привязки, команда, включение кнопки, undo/redo, сохранение и повторное открытие.

На Windows x64 прошли 12 нативных проверок из [ScriptAudioNativeTests](../../tests/NovellMaker.Editor.Tests/ScriptAudioNativeTests.cs) и [DialogueNativeTests](../../tests/NovellMaker.Editor.Tests/DialogueNativeTests.cs): новая позиция/пауза/продолжение/окончание/следующая реплика в F6 и self-contained экспорте, прежние Lua-ownership и save-barrier, отсутствие пауз/underrun потокового звука при update 5/20 Гц, одинаковые пиксели диалога в обоих хостах. Данные берутся из настоящего MonoGame backend; четырёхсекундный голос продвигается по потреблённым буферам и стоит на месте в паузе. Нативные тесты требуют `NOVELLMAKER_NATIVE_SMOKE=1`, экспортные — также `NOVELLMAKER_EXPORT_SMOKE=1` и актуальный Windows-шаблон. Runtime требуется запись тестового профиля/журнала в LocalAppData; отказ sandbox в этой записи даёт отдельную диагностику и не считается аудиодефектом.

Пределы: курсор и естественный fade-out имеют шаг существующего PCM-буфера, около 100 мс; позиция не сохраняется в слот, статический макет редактора не исполняет звук. Авторский Previous недоступен в режиме игрового прохождения экспортированной игры — этот аудиопатч не добавляет rollback. Отдельных Lua-команд fades/crossfade/ducking не добавлено, квоты и wire-протокол прежние. Нативная проверка Linux/macOS в рамках этой работы не выполнялась.

## Ожидание клипов и согласование справочника — 5 ноября 2026

GUI-ожидание «Завершение движений и клипов» и `game.wait_actions` используют общий расчёт завершения движений и явно запущенных конечных клипов, в том числе скрытых персонажей и конечных повторов. Клип позы по умолчанию и бесконечная петля не задерживают очередь. Форматы проекта v23, GameSave v4 и screen protocol v20 не меняются; оставшееся ожидание восстанавливается из времени сценария и таблиц клипов. Общий таймлайн фрагмента со звуком и событиями этим изменением не реализован.

[CharacterClipWaitTests](../../tests/NovellMaker.Core.Tests/Characters/CharacterClipWaitTests.cs) проверяют слоистый/покадровый клип, до 65535 конечных повторов, параллельное движение, большие/малые шаги времени, скрытие, смену позы/клипа, паузу, Load посреди ожидания, отклонение повреждённого снимка и `game.wait_actions` через настоящий Lua-worker. [ReadingPlaybackTests](../../tests/NovellMaker.Core.Tests/Execution/ReadingPlaybackTests.cs) дополнительно проверяют историю v3/v4, обязательность полей v4 и отказ Load без изменения живого состояния. Целевой прогон: **16/16**, без ошибок и пропусков. Смежный прогон до последних пяти дополнительных случаев: **286 Core** (7 пропущено) и **26 Editor** (4 native/export пропущено), ошибок нет. [GUI-тест](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) `StandardActionParametersAndExistingScriptReferenceCanBeChanged` проверяет выбор нового режима, применение и undo/redo в Avalonia Headless. [EditorRecoveryTests](../../tests/NovellMaker.Editor.Tests/EditorRecoveryTests.cs) включены в этот прогон; справочник больше не объявляет автосохранение отсутствующим. Проверка ссылок/покрытия справочника проходит. Нативный предпросмотр и экспорт для этого изменения повторно не запускались.

## Персонажи из частей и покадровые клипы — 1 ноября 2026

Проект v22 добавляет `CharacterState.Visual`, стабильные ID частей/клипов, родительскую иерархию, pivot, слои, ключи преобразования/opacity/замены изображения, линейную и ступенчатую интерполяцию, покадровые клипы с длительностями/повторами и действие `characterClip`. Библиотека персонажей содержит редактор числовых ключей, локальное воспроизведение и импорт GIF в PNG. GameSave v4 хранит выбранные клипы и локальное время; Load не переигрывает команды. [Правила и ограничения](characters.md).

[Core CharacterAnimationTests](../../tests/NovellMaker.Core.Tests/Characters/CharacterAnimationTests.cs) проверяют родительские матрицы и pivot, слой и наследование opacity, интерполяцию/ступеньку/замену изображения, границы цикла и конечных повторов, интервалы кадров, повреждённые графы/ключи, JSON round trip, явный запуск клипа, сброс на позу, паузу и Load без replay. [Editor CharacterAnimationTests](../../tests/NovellMaker.Editor.Tests/CharacterAnimationTests.cs) проверяют undo/redo/неизменяемость снимка, GIF с разными длительностями, композицию частичных кадров с RestorePrevious и отсутствие оставшихся кадров при ошибке. GUI-тест `CharacterPartsEditorAppliesPivotAndKeyAndRendersComposedPreview` в [EditorWindowTests](../../tests/NovellMaker.Editor.Tests/EditorWindowTests.cs) создаёт пример кнопкой, меняет pivot, записывает ключ, выполняет undo/redo, воспроизводит и повторно открывает сохранённые данные; сохранён кадр `artifacts/screenshots/character-animation/gui/editor-character-parts.png`.

Нативный `LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame` прогнан для двух представлений в F6-host и self-contained Windows x64: 4 случая, настоящие MonoGame PNG и GameSave. Состояние и матрицы частей после Load побайтно совпадают со снимком до него; проверены пиксели тела и дочерней головы. Готовая игра запущена с отсутствующим DOTNET_ROOT и системным PATH. Отчёты/PNG находятся в `artifacts/screenshots/character-animation/`. Это проверка Windows x64; Linux/macOS этим запуском не подтверждены. Валидация и прозрачность прежних статических персонажей/перетаскивание продолжают проверяться прежним GUI-тестом библиотеки персонажей.
