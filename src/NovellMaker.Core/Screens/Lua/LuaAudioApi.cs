using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Audio;

namespace NovellMaker.Core.Screens.Lua;

internal static class LuaAudioApi
{
    internal static void Install(Script script, Table ui, ImmutableDictionary<string, Guid> references,
        Func<bool> handling, Func<ScreenChange, DynValue> add)
    {
        var assets = new Table(script);
        foreach (var pair in references) assets.Set(pair.Key, DynValue.NewString(pair.Value.ToString("N")));
        script.Globals.Set("audio", DynValue.NewTable(assets));
        string Text(DynValue value) => value.Type == DataType.String ? value.String : throw new ScriptRuntimeException("Ожидалась строка звука.");
        Guid Id(DynValue value) => Guid.TryParseExact(Text(value), "N", out var id) && id != Guid.Empty ? id : throw new ScriptRuntimeException("Ожидался ID звука.");
        double Volume(DynValue value) => value.Type == DataType.Number && double.IsFinite(value.Number) && value.Number is >= 0 and <= 1
            ? value.Number : throw new ScriptRuntimeException("Громкость: число 0–1.");
        void Bind(string name, int count, Func<CallbackArguments, DynValue> action) => ui.Set(name, DynValue.NewCallback((_, args) =>
        {
            if (!handling() || args.Count != count) throw new ScriptRuntimeException("Звук ожидает обработчик и точное число аргументов.");
            return action(args);
        }));
        DynValue Add(ScriptAudioCommand command) => add(new("audio", null, null, null) { Audio = command });
        Bind("play_audio", 4, args =>
        {
            var asset = Id(args[0]);
            if (!references.ContainsValue(asset)) throw new ScriptRuntimeException("Звук не объявлен в ссылках Lua-ресурса.");
            var group = Text(args[1]) switch { "sound" => AudioGroup.Sound, "effect" => AudioGroup.Effect, "voice" => AudioGroup.Voice,
                _ => throw new ScriptRuntimeException("Группа звука: sound/effect/voice.") };
            if (args[2].Type != DataType.Boolean) throw new ScriptRuntimeException("Повтор звука: boolean.");
            var id = Guid.NewGuid(); Add(new("play", id, asset, group, args[2].Boolean, Volume(args[3])));
            return DynValue.NewString(id.ToString("N"));
        });
        Bind("stop_audio", 1, args => Add(new("stop", Id(args[0]))));
        Bind("set_audio_volume", 2, args => Add(new("volume", Id(args[0]), Volume: Volume(args[1]))));
    }
}
