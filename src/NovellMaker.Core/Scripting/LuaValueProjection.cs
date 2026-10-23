using System.Runtime.CompilerServices;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;

namespace NovellMaker.Core.Scripting;

/// <summary>Worker-only conversion. Every read produces detached Lua tables.</summary>
internal sealed class LuaValueProjection(Script script)
{
    private sealed record Shape(bool List);
    private sealed class NullMarker;
    // Explicit descriptor only: no reflection descriptor or global CLR type registration.
    private sealed class NullDescriptor : IUserDataDescriptor
    {
        public string Name => "ui.null";
        public Type Type => typeof(NullMarker);
        public DynValue Index(Script script, object obj, DynValue index, bool isDirectIndexing) => DynValue.Nil;
        public bool SetIndex(Script script, object obj, DynValue index, DynValue value, bool isDirectIndexing)
            => throw new ScriptRuntimeException("ui.null нельзя изменять.");
        public string AsString(object obj) => "ui.null";
        public DynValue? MetaIndex(Script script, object obj, string metaname) => null;
        public bool IsTypeCompatible(Type type, object obj) => type == typeof(NullMarker) && obj is NullMarker;
    }
    private readonly ConditionalWeakTable<Table, Shape> shapes = new();
    internal DynValue Null { get; } = UserData.Create(new NullMarker(), new NullDescriptor());

    internal DynValue Mark(DynValue value, bool list)
    {
        if (value.Type != DataType.Table)
            throw new ScriptRuntimeException("Ожидалась таблица данных Lua.");
        shapes.Remove(value.Table); shapes.Add(value.Table, new(list));
        return value;
    }

    internal LuaValue Read(DynValue value)
    {
        var path = new HashSet<Table>(ReferenceEqualityComparer.Instance);
        var count = 0; var characters = 0;
        LuaValue Visit(DynValue current, int depth)
        {
            if (++count > LuaValue.MaximumValues) throw new ScriptRuntimeException("Превышен бюджет элементов данных Lua.");
            if (current.Type == DataType.UserData && ReferenceEquals(current.UserData, Null.UserData)) return LuaValue.Null;
            switch (current.Type)
            {
                case DataType.Nil: case DataType.Void: return LuaValue.Null;
                case DataType.Boolean: return LuaValue.FromBoolean(current.Boolean);
                case DataType.Number: return LuaValue.FromNumber(current.Number);
                case DataType.String:
                    if ((characters += current.String.Length) > LuaValue.MaximumCharacters) throw new ScriptRuntimeException("Превышен бюджет строк данных Lua.");
                    return LuaValue.FromString(current.String);
                case DataType.Table:
                    var table = current.Table;
                    if (++depth > LuaValue.MaximumDepth || table.MetaTable is not null || !path.Add(table))
                        throw new ScriptRuntimeException("Цикл, метатаблица или превышение глубины данных Lua.");
                    try
                    {
                        var entries = new List<KeyValuePair<DynValue, DynValue>>();
                        foreach (var pair in table.Pairs)
                        {
                            // MoonSharp retains nil/void tombstones in Pairs after deletion.
                            if (pair.Value.IsNil()) continue;
                            if (entries.Count >= LuaValue.MaximumValues - count) throw new ScriptRuntimeException("Превышен бюджет элементов данных Lua.");
                            entries.Add(new(pair.Key, pair.Value));
                        }
                        var list = shapes.TryGetValue(table, out var shape) ? shape.List : entries.Count != 0 && entries[0].Key.Type == DataType.Number;
                        if (list)
                        {
                            var ordered = new LuaValue[entries.Count];
                            foreach (var pair in entries)
                            {
                                if (pair.Key.Type != DataType.Number || pair.Key.Number != Math.Truncate(pair.Key.Number) ||
                                    pair.Key.Number < 1 || pair.Key.Number > entries.Count)
                                    throw new ScriptRuntimeException("Список Lua должен иметь ключи 1..N без пропусков.");
                                ordered[(int)pair.Key.Number - 1] = Visit(pair.Value, depth);
                            }
                            return LuaValue.FromList(ordered);
                        }
                        var properties = new List<KeyValuePair<string, LuaValue>>();
                        foreach (var pair in entries)
                        {
                            if (pair.Key.Type != DataType.String || pair.Key.String.Length > LuaValue.MaximumKeyLength ||
                                (characters += pair.Key.String.Length) > LuaValue.MaximumCharacters)
                                throw new ScriptRuntimeException("Словарь Lua требует ограниченных строковых ключей.");
                            properties.Add(new(pair.Key.String, Visit(pair.Value, depth)));
                        }
                        return LuaValue.FromObject(properties);
                    }
                    finally { path.Remove(table); }
                default: throw new ScriptRuntimeException("Значение Lua не является сериализуемыми данными.");
            }
        }
        return Visit(value, 0);
    }

    internal DynValue Write(LuaValue value)
    {
        switch (value.Kind)
        {
            case LuaValueKind.Null: return Null;
            case LuaValueKind.Boolean: return DynValue.NewBoolean(value.Boolean);
            case LuaValueKind.Number: return DynValue.NewNumber(value.Number);
            case LuaValueKind.String: return DynValue.NewString(value.String);
            default:
                var table = new Table(script);
                if (value.Kind == LuaValueKind.List)
                    for (var i = 0; i < value.Items.Length; i++) table.Set(i + 1, Write(value.Items[i]));
                else foreach (var pair in value.Properties!) table.Set(pair.Key, Write(pair.Value));
                return Mark(DynValue.NewTable(table), value.Kind == LuaValueKind.List);
        }
    }
}
