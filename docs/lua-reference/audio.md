# Звук Lua

[Оглавление](index.md) · [Модель исполнения](execution-model.md) · [События](events.md) · [Квоты](limits.md)

Контекст: только активные ui-on_load/on_event и вызванные ими модули. Сценарного Lua audio API нет: фон/озвучку фразы назначают в редакторе. [Пакет](execution-model.md#batch), [владение](execution-model.md#namespaces), [lifetime](execution-model.md#lifetime), [квоты](limits.md#audio) и [audio_result](events.md#audio-result) применяются к каждой записи. Все три функции требуют **точное** число и типы аргументов.

Ресурс: зарегистрированный PCM WAV signed 16-bit, mono/stereo, 8000–48000 Гц. В свойствах корневого Lua-ресурса добавьте ссылку click на WAV; тогда `audio.click` — ID N. Это не путь, не имя файла и не handle узла. В модуль передайте ID параметром; его собственная глобальная audio отсутствует. Проигрывание ограничено 32 одновременными источниками всего AudioService, включая сюжетный звук. Усиление source × group × Master, mute групп также действует.

Источник принадлежит session/generation/owner/instance: stop/volume другого экземпляра запрещены. Ticket остаётся учтённым до commit его audio_result; после этого ID устарел. Закрытие/ошибка/Reset останавливают собственные источники без возрождения callback в новом экземпляре. Pause/покрытие/потеря host-фокуса удерживают звук. Исключение: обычный pending update не останавливает уже играющий звук, pending input/result/load удерживает. Звук не содержит Lua-позиции воспроизведения и не сохраняется в слот; мини-игру можно сохранить лишь после остановки/завершения звука и обработки результата. В restore on_load все три функции запрещены.

Lua не предоставляет fades, crossfade, ducking, позицию аудио или ожидание аудио через game.wait_actions. Конец обрабатывают событием. Ошибка после принятия host-команды (например, backend) возвращается failed; визуальные изменения уже принятого пакета из-за неё не откатываются.

<a id="ui-play-audio"></a>
## ui.play_audio

Запустить собственный зарегистрированный WAV.

```lua
ui.play_audio(assetId, group, loop, volume) -> audio_id
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно 4 аргумента. assetId: строка GUID N из audio.<alias> корневого обработчика; group: точно sound/effect/voice, Master недопустим. loop: boolean; volume: конечное число [0,1]. Все обязательны; nil не означает default.

**Возврат:** Сразу ID источника N; после commit и проигрывания одно audio_result с completed/stopped/failed.

**Эффект и ограничения:** После commit host открывает WAV. Ошибка файла/backend/лимита 32 источников на AudioService даёт failed, уже принятый визуальный пакет не откатывается. Создаётся резерв результата очереди. В restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local id = ui.play_audio(audio.click, "effect", false, 0.7)
```

**Связи:** [ui.stop_audio](audio.md#ui-stop-audio), [ui.set_audio_volume](audio.md#ui-set-audio-volume). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAudioApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScriptAudioLuaTests.cs).

<a id="ui-stop-audio"></a>
## ui.stop_audio

Остановить свой источник звука.

```lua
ui.stop_audio(audio_id) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно один GUID N, принадлежащий ещё учитываемому audio-ticket этого экземпляра. Чужой/уже удалённый ticket отвергает пакет.

**Возврат:** nil; позднее audio_result(status="stopped"), если источник ещё играл.

**Эффект и ограничения:** Команда пакета. Повторная остановка учтённого уже завершённого источника не создаёт второй результат. Ticket удаляется после commit обработчика его результата; слоты звук не сохраняют. Restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local id=ui.play_audio(audio.click, "effect", true, 0.7)
ui.stop_audio(id)
```

**Связи:** [ui.play_audio](audio.md#ui-play-audio). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAudioApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScriptAudioLuaTests.cs).

<a id="ui-set-audio-volume"></a>
## ui.set_audio_volume

Изменить усиление собственного источника.

```lua
ui.set_audio_volume(audio_id, volume) -> nil
```

**Доступность:** on_load/on_event ui, вызванный ими модуль, мини-игра; при restore — ограничения этой записи и [restore](execution-model.md#restore). Вычисление исходника вне обработчика не предназначено для команд.

**Аргументы и ошибки:** Ровно два аргумента; GUID N как stop_audio; volume: конечное число [0,1], nil запрещён.

**Возврат:** nil; отдельного события громкости нет.

**Эффект и ограничения:** Команда пакета. На учтённом завершённом источнике — no-op. Умножается на громкость группы/Master, профиль не пишет. Restore on_load запрещено. Общие [квоты](limits.md), [атомарность](execution-model.md#batch) и [lifecycle/сохранение](execution-model.md#lifetime) применяются, если здесь не указано исключение.

**Пример** (предусловия refs/ресурсов приведены в начале страницы; это фрагмент соответствующего обработчика, если не показан полный return):

```lua
local id=ui.play_audio(audio.click, "effect", true, 0.7)
ui.set_audio_volume(id, 0.2)
```

**Связи:** [ui.play_audio](audio.md#ui-play-audio), [ui.set_setting](session-and-storage.md#ui-set-setting). [Регистрация и аргументы](../../src/NovellMaker.Core/Screens/Lua/LuaAudioApi.cs), [тесты](../../tests/NovellMaker.Core.Tests/Screens/ScriptAudioLuaTests.cs).
