# NovellMaker

NovellMaker — самостоятельный редактор и движок визуальных новелл: C#/.NET 10,
Avalonia, MonoGame и ограниченный Lua 5.2/MoonSharp. Сцены, интерфейсы,
ресурсы и игровые правила принадлежат проекту автора.

Начните с [руководства автора](docs/lua-reference/engine-guide.md)
и [полного справочника Lua](docs/lua-reference/index.md).
В справочнике 109 функций, модель исполнения, все события, квоты и
проверяемые примеры сценариев, интерфейсов и мини-игр.
Ограничения проверки: [verification.md](docs/lua-reference/verification.md).

Для запуска из исходников нужен SDK из global.json и доступные закреплённые NuGet-пакеты:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet restore NovellMaker.slnx --locked-mode
dotnet run --project src/NovellMaker.Editor
```

Проверка кода: `./scripts/check.ps1`.
Проверка полноты справочника и ссылок: `python scripts/check-lua-reference.py`.
Поставка Windows-шаблона/NSIS, флаги нативных тестов и ограничения платформ
описаны в [руководстве](docs/lua-reference/engine-guide.md).
Игроку автономной Windows x64 сборки редактор и SDK не нужны.
