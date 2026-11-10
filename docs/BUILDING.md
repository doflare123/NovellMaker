# Сборка NovellMaker

Эта инструкция предназначена для сборки редактора из исходников.
Автору новеллы готовая поставка SDK не требует.

## Пакеты 0.1 beta

Нужны .NET SDK из `global.json` и PowerShell 7.

```powershell
./scripts/build-release.ps1 -Platform Windows
./scripts/build-release.ps1 -Platform Linux
```

Windows-пакет проверяется обязательным набором Delivery и включает автономный
runtime, Windows-шаблон экспорта, NSIS, руководство и примеры. Нужен Windows x64
с работающей графикой и звуком. Итоговые EXE-установщик, portable ZIP,
распакованная папка `NovellMaker`, контрольные суммы и qualification
сохраняются в `artifacts/releases/0.1.0-beta.1/`.

Установщик редактора дополнительно проверяется установкой в отдельную тестовую
папку, побайтовым сравнением payload, запуском без SDK, проверкой ярлыка и удалением
с сохранением пользовательских проектов и профиля. GitHub workflow прикрепляет
EXE, portable ZIP, Linux tar.gz и SHA256SUMS к черновику prerelease.
Файлы для ручного прикрепления к GitHub Release и готовый текст описания находятся
в `artifacts/github-release/0.1.0-beta.1/`; служебные отчёты туда не копируются.

Linux-пакет можно собрать на Windows, но запуск необходимо проверить на Linux.
Он включает автономный редактор и предпросмотр, без Windows-шаблона и NSIS.
Скрипт создаёт tar.gz с исполняемыми битами Linux-host. Кросс-сборка не считается
нативным допуском; в метаданных это отмечено явно.

Ни одна команда сборки не публикует файлы в GitHub автоматически.

## Для сборки редактора из репозитория

Нужен SDK, закреплённый в [global.json](../global.json), и доступные NuGet-пакеты; текущая база SDK 10.0.204. Core/Editor/Runtime написаны на C#, UI редактора Avalonia, runtime MonoGame. Зависимости зафиксированы lock-файлами; не подменяйте версию MoonSharp ради написания сценария.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet restore NovellMaker.slnx --locked-mode
dotnet run --project src/NovellMaker.Editor
```

Выпуск Windows x64: `./scripts/check-release.ps1` (офлайн NSIS: `-NsisArchive <путь>`). Скрипт готовит ZIP self-contained редактора с автономным Runtime, ExportTemplates и NSIS, распаковывает его в новый каталог и проверяет запуск окна поставленного редактора без SDK в PATH/DOTNET_ROOT, затем выполняет обязательный набор Delivery: создание независимого проекта командой редактора «Пример» → Save As/повторное открытие → настоящий предпросмотр → экспорт установщика → установка → запуск без SDK в PATH/DOTNET_ROOT → сохранение/загрузка → удаление с побайтовой проверкой всех пользовательских файлов → повторная установка и загрузка прежнего слота. В набор входят также рисование/аудиобэкенд, чтение/история, восстановление слоистой и покадровой анимации персонажей, реальный курсор/пауза/продолжение/окончание голоса — всего 11 обязательных случаев. Проверки новых функций используют предпросмотр и экспортный шаблон из того же распакованного ZIP. Создание/сохранение вызываются через те же команды EditorSession, что GUI; диалоги автоматизированы, это не проверка кликов по всему интерфейсу.

[Release delivery gate](../.github/workflows/release.yml) запускается для тегов `v*` и вручную. Он повторно выполняет [CI](../.github/workflows/ci.yml) на Windows/Linux/macOS и Windows Delivery для одного коммита. Отсутствующий TRX, пустой набор, пропуск или inconclusive обязательного теста блокируют выпуск. Только после успеха обоих заданий появляется `release-ready-win-x64`; для тега создаётся черновик GitHub Release с проверенным ZIP редактора и `qualification.json` (коммит, признак правок рабочей копии, SHA-256 ZIP, этапы, время). В CI изменившаяся рабочая копия запрещена; локальный запуск честно фиксирует её правки в worktreeDirty. Во время публикации ZIP не пересобирается. Черновик публикуется ответственным за выпуск после просмотра. Ручные загрузки через GitHub вне workflow технически не запрещены этим YAML; права выпуска/защита тегов задаются настройками репозитория.

TRX каждого запуска находятся в уникальном `artifacts/release/<run>/TestResults`; JSON/PNG нативных проверок — `artifacts/release/<run>/evidence`, в CI сохраняются и при сбое. Скрипт не выдаёт успешный ZIP/qualification при сбое проверки. Обычный `check.ps1` и задание `test` CI выполняют поведенческие проверки; их успех не заменяет выпускной допуск. Нативный экспорт Linux/macOS этим допуском не подтверждается.

Нативная переносимость runtime: `./scripts/check-native-portability.ps1` на целевой ОС (Linux с графической сессией либо `xvfb-run -a pwsh -NoProfile -File ./scripts/check-native-portability.ps1`). Скрипт публикует self-contained runtime для текущей архитектуры, проверяет графику/аудио, screen Lua UI, бюджет game Lua и завершение worker при отмене; требует все 6 случаев без пропусков. В CI есть отдельные Linux/macOS задания, от которых также зависит выпуск Windows. Evidence сохраняется в `artifacts/native-portability/<rid>/<run>`; `portability.json` явно отделяет этот прогон от проверки экспорта. Linux CI использует software GL и OpenAL null backend. Экспорт Linux/macOS и общая ОС-квота памяти их Lua worker остаются открытыми; добавленный CI не является свидетельством нативного успеха до его выполнения. Границы платформ описаны в [совместимости и ограничениях](lua-reference/compatibility.md).

Поставка runtime-шаблона: `./scripts/prepare-windows-export.ps1`. Поставка с NSIS: `./scripts/prepare-windows-installer.ps1` (официальный ZIP проверяется закреплённым SHA-256; офлайн `-NsisArchive <путь>`). Build/Publish включают подготовленные файлы. Полные проверки: `./scripts/check.ps1`; нативные флаги `-CaptureScreenshots -NativeRuntime -WindowsExport` либо `-WindowsInstaller` запускают соответствующие более широкие проверки; `-ReleaseLive` включает реальный сетевой smoke. Проверка этого справочника: `python scripts/check-lua-reference.py`.

