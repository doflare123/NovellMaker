using System.Globalization;
using System.Text;
using MoonSharp.Interpreter;

namespace NovellMaker.Core.Scripting;

/// <summary>Explicit component-only allowlist. No stock modules, callbacks into Lua, patterns or ambient state.</summary>
internal sealed class LuaStandardLibrary(Script script)
{
    internal const int MaximumStringLength = 8192;
    internal const int MaximumTableEntries = 1024;
    internal const int MaximumWork = 65536;
    internal const int MaximumResultCharacters = 32768;
    private int work;
    private int characters;

    internal void ResetBudget() { work = 0; characters = 0; }

    private void Charge(long count)
    {
        if (count > MaximumWork - work) throw new ScriptRuntimeException("Превышен бюджет операций стандартной библиотеки Lua (65536).");
        work += (int)count;
    }

    private void ReserveText(long count)
    {
        if (count > MaximumStringLength) throw new ScriptRuntimeException("Результат стандартной библиотеки Lua длиннее 8192 символов.");
        if (count > MaximumResultCharacters - characters) throw new ScriptRuntimeException("Превышен бюджет строк стандартной библиотеки Lua (32768).");
        Charge(count);
        characters += (int)count;
    }

    private static string Text(DynValue value)
    {
        if (value.Type != DataType.String || value.String.Length > MaximumStringLength)
            throw new ScriptRuntimeException("Ожидалась строка не длиннее 8192 символов.");
        return value.String;
    }

    private static double Number(DynValue value)
    {
        if (value.Type != DataType.Number || !double.IsFinite(value.Number))
            throw new ScriptRuntimeException("Ожидалось конечное число.");
        return value.Number;
    }

    private static int Integer(DynValue value)
    {
        var n = Number(value);
        if (n < int.MinValue || n > int.MaxValue || n != Math.Truncate(n))
            throw new ScriptRuntimeException("Ожидалось целое число int32.");
        return (int)n;
    }

    private static DynValue Finite(double value) => double.IsFinite(value) ? DynValue.NewNumber(value) :
        throw new ScriptRuntimeException("Результат math должен быть конечным числом.");

    private static string Scalar(DynValue value) => value.Type switch
    {
        DataType.Nil or DataType.Void => "nil",
        DataType.Boolean => value.Boolean ? "true" : "false",
        DataType.Number => Number(value).ToString("G", CultureInfo.InvariantCulture),
        DataType.String => Text(value),
        _ => throw new ScriptRuntimeException("tostring принимает только nil, boolean, number или string."),
    };

    private List<TablePair> Entries(DynValue value)
    {
        if (value.Type != DataType.Table || value.Table.MetaTable is not null)
            throw new ScriptRuntimeException("Ожидалась таблица без метатаблицы.");
        var result = new List<TablePair>();
        var visited = 0;
        foreach (var pair in value.Table.Pairs)
        {
            // Count physical entries too: a long-lived table can contain removed keys.
            if (++visited > MaximumTableEntries) throw new ScriptRuntimeException("Таблица стандартной библиотеки Lua содержит больше 1024 записей.");
            Charge(1);
            if (!pair.Value.IsNil()) result.Add(pair);
        }
        return result;
    }

    private int DenseLength(DynValue value)
    {
        var entries = Entries(value);
        foreach (var pair in entries)
            if (pair.Key.Type != DataType.Number || pair.Key.Number < 1 || pair.Key.Number > entries.Count || pair.Key.Number != Math.Truncate(pair.Key.Number))
                throw new ScriptRuntimeException("Ожидался плотный список с ключами 1..N.");
        return entries.Count;
    }

    internal void Install(Table environment)
    {
        void Bind(Table target, string name, int minimum, int maximum, Func<CallbackArguments, DynValue> callback) =>
            target.Set(name, DynValue.NewCallback((_, args) =>
            {
                if (args.Count < minimum || args.Count > maximum)
                    throw new ScriptRuntimeException($"{name}: ожидалось от {minimum} до {maximum} аргументов.");
                Charge(1);
                return callback(args);
            }));
        var math = new Table(script);
        void Unary(string name, Func<double, double> operation) => Bind(math, name, 1, 1, a => Finite(operation(Number(a[0]))));
        void Binary(string name, Func<double, double, double> operation) => Bind(math, name, 2, 2, a => Finite(operation(Number(a[0]), Number(a[1]))));
        Unary("abs", Math.Abs); Unary("ceil", Math.Ceiling); Unary("floor", Math.Floor);
        Unary("sqrt", Math.Sqrt); Unary("sin", Math.Sin); Unary("cos", Math.Cos); Unary("tan", Math.Tan);
        Unary("asin", Math.Asin); Unary("acos", Math.Acos); Unary("atan", Math.Atan); Unary("exp", Math.Exp);
        Unary("deg", n => n * (180 / Math.PI)); Unary("rad", n => n * (Math.PI / 180));
        Binary("atan2", Math.Atan2); Binary("pow", Math.Pow); Binary("fmod", (x, y) => x % y);
        Bind(math, "log", 1, 2, a => Finite(a.Count == 1 ? Math.Log(Number(a[0])) : Math.Log(Number(a[0]), Number(a[1]))));
        Bind(math, "modf", 1, 1, a => { var n = Number(a[0]); return DynValue.NewTuple(Finite(Math.Truncate(n)), Finite(n - Math.Truncate(n))); });
        foreach (var name in new[] { "min", "max" })
            Bind(math, name, 1, 64, a =>
            {
                Charge(a.Count);
                var n = Number(a[0]);
                for (var i = 1; i < a.Count; i++) n = name == "min" ? Math.Min(n, Number(a[i])) : Math.Max(n, Number(a[i]));
                return Finite(n);
            });
        math.Set("pi", DynValue.NewNumber(Math.PI));
        environment.Set("math", DynValue.NewTable(math));

        var strings = new Table(script);
        Bind(strings, "len", 1, 1, a => DynValue.NewNumber(Text(a[0]).Length));
        Bind(strings, "sub", 2, 3, a =>
        {
            var text = Text(a[0]);
            var start = Index(Integer(a[1]), text.Length);
            var end = a.Count == 2 ? text.Length : Index(Integer(a[2]), text.Length);
            start = Math.Max(1, start); end = Math.Min(text.Length, end);
            var count = Math.Max(0, end - start + 1);
            ReserveText(count);
            return DynValue.NewString(count == 0 ? "" : text.Substring((int)start - 1, (int)count));
        });
        foreach (var name in new[] { "lower", "upper", "reverse" })
            Bind(strings, name, 1, 1, a =>
            {
                var text = Text(a[0]); ReserveText(text.Length);
                if (name == "lower") return DynValue.NewString(text.ToLowerInvariant());
                if (name == "upper") return DynValue.NewString(text.ToUpperInvariant());
                var chars = text.ToCharArray(); Array.Reverse(chars); return DynValue.NewString(new string(chars));
            });
        Bind(strings, "rep", 2, 3, a =>
        {
            var text = Text(a[0]); var count = Integer(a[1]); var separator = a.Count == 3 ? Text(a[2]) : "";
            if (count < 0 || count > MaximumStringLength) throw new ScriptRuntimeException("string.rep: число повторов от 0 до 8192.");
            var length = (long)text.Length * count + (long)separator.Length * Math.Max(0, count - 1);
            Charge(count); ReserveText(length);
            var result = new StringBuilder((int)length);
            for (var i = 0; i < count; i++) { if (i > 0) result.Append(separator); result.Append(text); }
            return DynValue.NewString(result.ToString());
        });
        Bind(strings, "find", 2, 4, a =>
        {
            var text = Text(a[0]); var needle = Text(a[1]);
            if (a.Count == 4 && (a[3].Type != DataType.Boolean || !a[3].Boolean))
                throw new ScriptRuntimeException("string.find поддерживает только буквальный поиск (plain=true).");
            var start = a.Count >= 3 ? Index(Integer(a[2]), text.Length) : 1;
            start = Math.Max(1, start);
            Charge((long)text.Length * Math.Max(1, needle.Length));
            if (start > text.Length + 1L) return DynValue.Nil;
            var position = text.IndexOf(needle, (int)start - 1, StringComparison.Ordinal);
            return position < 0 ? DynValue.Nil : DynValue.NewTuple(DynValue.NewNumber(position + 1), DynValue.NewNumber(position + needle.Length));
        });
        environment.Set("string", DynValue.NewTable(strings));

        var tables = new Table(script);
        Bind(tables, "insert", 2, 3, a =>
        {
            var length = DenseLength(a[0]); var position = a.Count == 2 ? length + 1 : Integer(a[1]); var value = a[a.Count - 1];
            if (length >= MaximumTableEntries || position < 1 || position > length + 1 || value.IsNil())
                throw new ScriptRuntimeException("table.insert: неверная позиция, nil или переполнение списка.");
            Charge(length + 1);
            for (var i = length; i >= position; i--) a[0].Table.Set(i + 1, a[0].Table.Get(i));
            a[0].Table.Set(position, value); return DynValue.Nil;
        });
        Bind(tables, "remove", 1, 2, a =>
        {
            var length = DenseLength(a[0]); var position = a.Count == 1 ? length : Integer(a[1]);
            if (length == 0 && a.Count == 1) return DynValue.Nil;
            if (position < 1 || position > length) throw new ScriptRuntimeException("table.remove: позиция вне списка.");
            Charge(length);
            var removed = a[0].Table.Get(position);
            for (var i = position; i < length; i++) a[0].Table.Set(i, a[0].Table.Get(i + 1));
            a[0].Table.Set(length, DynValue.Nil); return removed;
        });
        Bind(tables, "concat", 1, 4, a =>
        {
            var length = DenseLength(a[0]); var separator = a.Count >= 2 ? Text(a[1]) : "";
            var start = a.Count >= 3 ? Integer(a[2]) : 1; var end = a.Count == 4 ? Integer(a[3]) : length;
            if (start < 1 || start > length + 1 || end < 0 || end > length)
                throw new ScriptRuntimeException("table.concat: диапазон вне списка.");
            var parts = new List<string>(); long size = 0;
            for (var i = start; i <= end; i++)
            {
                var item = a[0].Table.Get(i);
                if (item.Type is not (DataType.String or DataType.Number)) throw new ScriptRuntimeException("table.concat: элементы должны быть строками или числами.");
                var part = Scalar(item); size += part.Length; parts.Add(part);
            }
            size += (long)Math.Max(0, parts.Count - 1) * separator.Length;
            ReserveText(size);
            return DynValue.NewString(string.Join(separator, parts));
        });
        environment.Set("table", DynValue.NewTable(tables));

        Bind(environment, "type", 1, 1, a =>
        {
            var text = a[0].Type switch
            {
            DataType.Nil or DataType.Void => "nil", DataType.Boolean => "boolean", DataType.Number => "number", DataType.String => "string",
            DataType.Table => "table", DataType.Function or DataType.ClrFunction => "function", _ => "userdata",
            };
            ReserveText(text.Length); return DynValue.NewString(text);
        });
        Bind(environment, "tostring", 1, 1, a => { var text = Scalar(a[0]); ReserveText(text.Length); return DynValue.NewString(text); });
        Bind(environment, "tonumber", 1, 1, a =>
        {
            if (a[0].Type == DataType.Number) return Finite(Number(a[0]));
            if (a[0].Type != DataType.String) return DynValue.Nil;
            var text = Text(a[0]); Charge(text.Length);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? DynValue.NewNumber(n) : DynValue.Nil;
        });
        Bind(environment, "pairs", 1, 1, a =>
        {
            var entries = Entries(a[0]); var index = 0;
            return DynValue.NewCallback((_, _) =>
            {
                Charge(1);
                return index == entries.Count ? DynValue.Nil : Pair(entries[index++]);
            });
        });
        Bind(environment, "ipairs", 1, 1, a =>
        {
            var length = DenseLength(a[0]); var values = new DynValue[length];
            for (var i = 0; i < length; i++) values[i] = a[0].Table.Get(i + 1);
            var index = 0;
            return DynValue.NewCallback((_, _) =>
            {
                Charge(1);
                return index == values.Length ? DynValue.Nil : DynValue.NewTuple(DynValue.NewNumber(++index), values[index - 1]);
            });
        });
    }

    private static long Index(int index, int length) => index < 0 ? (long)length + index + 1 : index;
    private static DynValue Pair(TablePair pair) => DynValue.NewTuple(pair.Key, pair.Value);
}
