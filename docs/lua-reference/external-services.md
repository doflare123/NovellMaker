# Внешний сервис проверки выпуска

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Контекст: ui-обработчик/модуль, никогда game. Сервис реализован на host, Lua не получает HTTP-клиент. Для каждой записи действуют [пакет](execution-model.md#batch), [жизненный цикл](execution-model.md#lifetime), [схема результата и все code](events.md#release-result), [квоты](limits.md#external). В restore on_load обе операции запрещены.

В параметрах проекта включите `github_releases_v1`, задайте публичные Owner, Repository и CurrentVersion. Owner 1..39 ASCII alnum/-, без двойного дефиса и дефиса на краях; Repository 1..100 alnum/_.-, кроме . / .. и суффикса .git; токены вместо имени запрещены. Версия: 3 или 4 десятичных компонента 0..65535 без ведущих нулей; результат канонизируется в 4 компонента. Тег выпуска допускает один начальный строчный v. Prerelease/draft не принимаются.

Запрос только к `https://api.github.com/repos/<Owner>/<Repository>/releases/latest`, без credentials/cookies/redirects/автоповтора. Ответ — проверенные данные страницы GitHub того же репозитория; page_url не открывается автоматически. Нет произвольного URL, сети, установки обновлений, отправки телеметрии или секретов из Lua.

10 секунд от принятия запроса, включая ожидание; 1 HTTP активный+4 ожидающих на сервис. Интервал HTTP >=60 секунд, кеш успеха 15 минут (в памяти), Retry-After может увеличить запрет. Кеш/интервал переживают смену игрового сеанса того же host; не сохраняются в слот/профиль. До 128 учтённых ID на владельца, 128 резервов, 64 владельца; подтверждённые ID остаются для проверки отмены до закрытия владельца. Busy владельца удерживается до commit обработчика результата. Смена поколения/закрытие отменяет запросы и отбрасывает старые ответы. При паузе HTTP и deadline продолжаются, callback ждёт снятия паузы. Мини-игра не сохраняется при живом запросе/необработанном результате.

Host отклоняет пакет до сетевого эффекта при неправильном владельце/резерве/цепочке. Обычные provider-ошибки возвращаются событиями, а не останавливают обработчик. HTTP тело после распаковки <=256 КиБ, заголовки <=16 КиБ, JSON depth<=16; проекция release <=8192 байт, page_url<=512 символов.

<a id="ui-check-game-release"></a>
## ui.check_game_release

Проверить последний публичный полный выпуск игры на GitHub.

```lua
ui.check_game_release() -> request_id
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Ровно ноль аргументов; URL, токен и версия из Lua не принимаются. Нужна настроенная декларация проекта github_releases_v1.

**Возврат:** Сразу request_id N; затем service_result(operation="CheckGameRelease", provider="github_releases_v1", code=ReleaseCheckCode, release?=...).

**Эффект и ограничения:** Это один сервис пакета. Сеть начинается только после commit. Нет автоповтора, скачивания или открытия URL. Disabled/Busy/RateLimited/сбой провайдера — событие, а не ошибка Lua. Ошибка владения/квоты до commit отвергает весь пакет. Restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local request = ui.check_game_release()
```

**Связи:** [ui.cancel_release_check](external-services.md#ui-cancel-release-check). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ReleaseCheckLuaTests.cs).

<a id="ui-cancel-release-check"></a>
## ui.cancel_release_check

Отменить свой запрос проверки выпуска.

```lua
ui.cancel_release_check(request_id) -> nil
```

**Доступность:** ui on_load/on_event, модуль, мини-игра; в restore on_load запрещена. До возврата handlers сервисные команды запрещены.

**Аргументы и ошибки:** Ровно одна точная строка непустого GUID N своего запроса. Чужой/никогда не выданный ID — ошибка пакета.

**Возврат:** nil. Отдельного service_result для CancelReleaseCheck нет. Исходный незавершённый CheckGameRelease получит code=Cancelled.

**Эффект и ограничения:** Один сервис пакета, несовместим с другой сервисной командой. Уже завершённый/подтверждённый свой запрос — no-op. Закрытие экземпляра отменяет без callback. Restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local pending
return {
  on_load=function() pending=ui.check_game_release() end,
  on_event=function(e)
    if e.kind=="click" and pending~=nil then
      ui.cancel_release_check(pending)
    elseif e.kind=="service_result" and e.request_id==pending then
      pending=nil
    end
  end
}
```

**Связи:** [ui.check_game_release](external-services.md#ui-check-game-release). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ReleaseCheckLuaTests.cs).
