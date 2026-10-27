using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Screens.Animations;

namespace NovellMaker.Core.Screens.Lua;

/// <summary>Descriptions live only for one handler; no Lua code runs during interpolation.</summary>
internal sealed class LuaAnimationApi
{
    private readonly Dictionary<string, ImmutableArray<ScreenTween>> descriptions = [];
    internal void Reset() => descriptions.Clear();

    internal void Install(Script script, Table ui, Func<bool> handling, Func<CallbackArguments, Guid> node,
        Func<ScreenDefinition> definition, Func<ScreenChange, DynValue> add)
    {
        void Bind(string name, int count, Func<CallbackArguments, DynValue> action) => ui.Set(name, DynValue.NewCallback((_, args) =>
        {
            if (!handling() || args.Count != count) throw new ScriptRuntimeException("Анимация ожидает обработчик и точное число аргументов.");
            return action(args);
        }));
        string Text(DynValue value) => value.Type == DataType.String ? value.String : throw new ScriptRuntimeException("Ожидалась строка анимации.");
        DynValue Store(ImmutableArray<ScreenTween> tracks)
        {
            if (descriptions.Count >= 128 || tracks.Length > ScreenAnimation.MaximumTracks || tracks.IsEmpty || tracks.Max(t => t.Start + t.Seconds) > ScreenAnimation.MaximumSeconds)
                throw new ScriptRuntimeException("Превышен бюджет описаний анимаций.");
            var token = Guid.NewGuid().ToString("N"); descriptions.Add(token, tracks); return DynValue.NewString(token);
        }
        ImmutableArray<ScreenTween> Get(DynValue token) => descriptions.TryGetValue(Text(token), out var tracks) ? tracks :
            throw new ScriptRuntimeException("Чужое или устаревшее описание анимации.");
        // Optional easing is handled explicitly, while every other argument remains exact.
        ui.Set("tween", DynValue.NewCallback((_, args) =>
        {
            if (!handling() || args.Count is < 3 or > 4 || args[1].Type != DataType.Table || args[1].Table.MetaTable is not null || args[2].Type != DataType.Number)
                throw new ScriptRuntimeException("tween ожидает узел, таблицу свойств, секунды и необязательный easing.");
            var target = node(args); var seconds = args[2].Number; var easing = args.Count == 4 ? Text(args[3]) : "linear";
            var tracks = ImmutableArray.CreateBuilder<ScreenTween>();
            foreach (var pair in args[1].Table.Pairs)
            {
                if (pair.Value.IsNil()) continue;
                if (tracks.Count >= ScreenAnimation.MaximumTracks || pair.Key.Type != DataType.String || pair.Value.Type != DataType.Number)
                    throw new ScriptRuntimeException("Неверные свойства tween.");
                tracks.Add(new(target, pair.Key.String, pair.Value.Number, 0, seconds, easing));
            }
            // A description can become one child of a coordinated parallel plan.
            // Validate local values now; validate world geometry only for the complete animate command.
            var value = tracks.ToImmutable(); new ScreenAnimation(Guid.NewGuid(), value).Validate(definition(), validateTimeline: false); return Store(value);
        }));
        DynValue Group(CallbackArguments args, bool sequential)
        {
            if (args[0].Type != DataType.Table || args[0].Table.MetaTable is not null) throw new ScriptRuntimeException("Группа ожидает плотный список описаний.");
            var table = args[0].Table; var count = 0;
            foreach (var pair in table.Pairs.Where(p => !p.Value.IsNil()))
                if (++count > 64 || pair.Key.Type != DataType.Number || pair.Key.Number < 1 || pair.Key.Number > table.Length || pair.Key.Number != Math.Truncate(pair.Key.Number))
                    throw new ScriptRuntimeException("Неверный список группы анимаций.");
            if (count != table.Length) throw new ScriptRuntimeException("Разреженная группа анимаций.");
            var tracks = ImmutableArray.CreateBuilder<ScreenTween>(); var offset = 0d;
            for (var i = 1; i <= count; i++)
            {
                var child = Get(table.Get(i));
                if (tracks.Count + child.Length > ScreenAnimation.MaximumTracks) throw new ScriptRuntimeException("Превышен бюджет треков группы.");
                tracks.AddRange(child.Select(t => t with { Start = t.Start + offset }));
                if (sequential) offset += child.Max(t => t.Start + t.Seconds);
            }
            return Store(tracks.ToImmutable());
        }
        Bind("sequence", 1, a => Group(a, true)); Bind("parallel", 1, a => Group(a, false));
        Bind("animate", 1, a =>
        {
            var animation = new ScreenAnimation(Guid.NewGuid(), Get(a[0]));
            add(new("animate", null, null, null) { Animation = animation }); return DynValue.NewString(animation.Id.ToString("N"));
        });
        Bind("cancel_animation", 1, a =>
        {
            if (!Guid.TryParseExact(Text(a[0]), "N", out var id) || id == Guid.Empty) throw new ScriptRuntimeException("Ожидался ID анимации.");
            return add(new("cancel_animation", null, null, null) { AnimationId = id });
        });
    }
}
