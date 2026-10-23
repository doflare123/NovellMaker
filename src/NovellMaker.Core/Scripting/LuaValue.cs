using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Scripting;

public enum LuaValueKind { Null, Boolean, Number, String, List, Object }

/// <summary>Immutable, bounded data. Contains no VM, CLR objects or executable Lua.</summary>
[JsonConverter(typeof(LuaValueJsonConverter))]
public sealed class LuaValue
{
    public const int MaximumDepth = 16;
    public const int MaximumValues = 1024;
    public const int MaximumStringLength = 8192;
    public const int MaximumKeyLength = 128;
    public const int MaximumCharacters = 32768;
    public const int MaximumJsonBytes = 65536;

    private LuaValue(LuaValueKind kind, bool boolean = false, double number = 0, string? text = null,
        ImmutableArray<LuaValue> items = default, ImmutableSortedDictionary<string, LuaValue>? properties = null)
    { Kind = kind; Boolean = boolean; Number = number; String = text; Items = items; Properties = properties; }

    public LuaValueKind Kind { get; }
    public bool Boolean { get; }
    public double Number { get; }
    public string? String { get; }
    public ImmutableArray<LuaValue> Items { get; }
    public ImmutableSortedDictionary<string, LuaValue>? Properties { get; }
    public int JsonByteCount => Encoding.UTF8.GetByteCount(ToJson());

    public static LuaValue Null { get; } = new(LuaValueKind.Null);
    public static LuaValue FromBoolean(bool value) => new(LuaValueKind.Boolean, boolean: value);
    public static LuaValue FromNumber(double value) => double.IsFinite(value)
        ? new(LuaValueKind.Number, number: value) : throw new ArgumentException("Данные Lua требуют конечного числа.");
    public static LuaValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Checked(new(LuaValueKind.String, text: value));
    }
    public static LuaValue FromList(IEnumerable<LuaValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        // Never enumerate an unbounded producer merely to discover an invalid size.
        var items = values.Take(MaximumValues + 1).ToImmutableArray();
        return Checked(new(LuaValueKind.List, items: items));
    }
    public static LuaValue FromObject(IEnumerable<KeyValuePair<string, LuaValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var properties = ImmutableSortedDictionary.CreateBuilder<string, LuaValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (properties.Count >= MaximumValues || pair.Key is null || !properties.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Повторный ключ или превышен бюджет словаря Lua.");
        }
        return Checked(new(LuaValueKind.Object, properties: properties.ToImmutable()));
    }

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static LuaValue FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new ArgumentException("Превышен размер данных Lua.");
        return JsonSerializer.Deserialize<LuaValue>(json) ?? Null;
    }

    private static LuaValue Checked(LuaValue value)
    {
        var count = 0; var characters = 0;
        void Visit(LuaValue item, int depth)
        {
            if (item is null || ++count > MaximumValues) throw new ArgumentException("Превышен бюджет элементов данных Lua.");
            if (item.Kind is LuaValueKind.List or LuaValueKind.Object && ++depth > MaximumDepth)
                throw new ArgumentException("Превышена глубина данных Lua.");
            void Text(string text, int limit)
            {
                if (text.Length > limit || (characters += text.Length) > MaximumCharacters)
                    throw new ArgumentException("Превышен бюджет строк данных Lua.");
                // Unpaired surrogates would silently change during JSON/Lua round-trip.
                for (var i = 0; i < text.Length; i++)
                    if (char.IsSurrogate(text[i]) && (!char.IsHighSurrogate(text[i]) || ++i == text.Length || !char.IsLowSurrogate(text[i])))
                        throw new ArgumentException("Некорректный Unicode в данных Lua.");
            }
            if (item.Kind == LuaValueKind.String) Text(item.String!, MaximumStringLength);
            if (item.Kind == LuaValueKind.List) foreach (var child in item.Items) Visit(child, depth);
            if (item.Kind == LuaValueKind.Object) foreach (var pair in item.Properties!)
            { Text(pair.Key, MaximumKeyLength); Visit(pair.Value, depth); }
        }
        Visit(value, 0);
        if (value.JsonByteCount > MaximumJsonBytes) throw new ArgumentException("Превышен размер JSON данных Lua.");
        return value;
    }

    internal void Write(Utf8JsonWriter writer)
    {
        switch (Kind)
        {
            case LuaValueKind.Null: writer.WriteNullValue(); break;
            case LuaValueKind.Boolean: writer.WriteBooleanValue(Boolean); break;
            case LuaValueKind.Number: writer.WriteNumberValue(Number); break;
            case LuaValueKind.String: writer.WriteStringValue(String); break;
            case LuaValueKind.List:
                writer.WriteStartArray(); foreach (var item in Items) item.Write(writer); writer.WriteEndArray(); break;
            case LuaValueKind.Object:
                writer.WriteStartObject(); foreach (var pair in Properties!) { writer.WritePropertyName(pair.Key); pair.Value.Write(writer); }
                writer.WriteEndObject(); break;
        }
    }

    internal static LuaValue Read(ref Utf8JsonReader reader)
    {
        var count = 0; var characters = 0;
        LuaValue ReadValue(ref Utf8JsonReader input, int depth)
        {
            if (++count > MaximumValues) throw new JsonException("Превышен бюджет элементов данных Lua.");
            if (input.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject && ++depth > MaximumDepth)
                throw new JsonException("Превышена глубина данных Lua.");
            string Text(ref Utf8JsonReader token, int maximum)
            {
                // Bound before allocating an unescaped string, including malicious wire payloads.
                if (token.ValueSpan.Length > maximum * 6) throw new JsonException("Превышен размер строки данных Lua.");
                var text = token.GetString()!;
                if (text.Length > maximum || (characters += text.Length) > MaximumCharacters)
                    throw new JsonException("Превышен бюджет строк данных Lua.");
                return text;
            }
            switch (input.TokenType)
            {
                case JsonTokenType.Null: return Null;
                case JsonTokenType.True: return FromBoolean(true);
                case JsonTokenType.False: return FromBoolean(false);
                case JsonTokenType.Number:
                    if (!input.TryGetDouble(out var number) || !double.IsFinite(number)) throw new JsonException("Данные Lua требуют конечного числа.");
                    return FromNumber(number);
                case JsonTokenType.String: return new(LuaValueKind.String, text: Text(ref input, MaximumStringLength));
                case JsonTokenType.StartArray:
                    var items = ImmutableArray.CreateBuilder<LuaValue>();
                    while (input.Read() && input.TokenType != JsonTokenType.EndArray) items.Add(ReadValue(ref input, depth));
                    if (input.TokenType != JsonTokenType.EndArray) throw new JsonException("Незавершённый список Lua.");
                    return new(LuaValueKind.List, items: items.ToImmutable());
                case JsonTokenType.StartObject:
                    var properties = ImmutableSortedDictionary.CreateBuilder<string, LuaValue>(StringComparer.Ordinal);
                    while (input.Read() && input.TokenType != JsonTokenType.EndObject)
                    {
                        if (input.TokenType != JsonTokenType.PropertyName) throw new JsonException("Ожидался ключ данных Lua.");
                        var key = Text(ref input, MaximumKeyLength);
                        if (!input.Read()) throw new JsonException("Незавершённый словарь Lua.");
                        var child = ReadValue(ref input, depth);
                        if (!properties.TryAdd(key, child)) throw new JsonException("Повторный ключ данных Lua.");
                    }
                    if (input.TokenType != JsonTokenType.EndObject) throw new JsonException("Незавершённый словарь Lua.");
                    return new(LuaValueKind.Object, properties: properties.ToImmutable());
                default: throw new JsonException("Недопустимое значение данных Lua.");
            }
        }
        try { return Checked(ReadValue(ref reader, 0)); }
        catch (ArgumentException error) { throw new JsonException(error.Message, error); }
    }
}

internal sealed class LuaValueJsonConverter : JsonConverter<LuaValue>
{
    public override bool HandleNull => true;
    public override LuaValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => LuaValue.Read(ref reader);
    public override void Write(Utf8JsonWriter writer, LuaValue value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Используйте LuaValue.Null.");
        value.Write(writer);
    }
}
