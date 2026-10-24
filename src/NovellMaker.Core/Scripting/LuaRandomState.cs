using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Scripting;

/// <summary>Versioned immutable PCG32 snapshot. The stream is fixed; no ambient entropy or platform RNG.</summary>
[JsonConverter(typeof(LuaRandomStateJsonConverter))]
public sealed record LuaRandomState
{
    private const string Prefix = "pcg32-v1:";
    private LuaRandomState(ulong value) => Value = value;
    internal ulong Value { get; }
    public static LuaRandomState Initial { get; } = FromSeed(0);

    public static LuaRandomState FromSeed(uint seed)
    {
        ulong value = 0;
        Step(ref value); value = unchecked(value + seed); Step(ref value);
        return new(value);
    }

    public static LuaRandomState Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length != Prefix.Length + 16 || !text.StartsWith(Prefix, StringComparison.Ordinal) ||
            text.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Ожидался снимок RNG pcg32-v1 с 16 строчными hex-цифрами.");
        return new(ulong.Parse(text.AsSpan(Prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }

    public override string ToString() => Prefix + Value.ToString("x16", CultureInfo.InvariantCulture);
    internal static LuaRandomState Capture(ulong value) => new(value);

    // PCG XSH-RR algorithm and seeding: Melissa O'Neill, https://www.pcg-random.org/.
    // C# adaptation with a fixed stream (initseq=54). Copyright 2014 Melissa O'Neill.
    // Apache-2.0; full attribution/license distributed in Licenses/PCG.txt.
    internal static uint Step(ref ulong value)
    {
        var previous = value;
        value = unchecked(previous * 6364136223846793005UL + 109UL);
        var bits = unchecked((uint)(((previous >> 18) ^ previous) >> 27));
        return BitOperations.RotateRight(bits, (int)(previous >> 59));
    }
}

public sealed class LuaRandomStateJsonConverter : JsonConverter<LuaRandomState>
{
    public override LuaRandomState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Снимок RNG должен быть строкой.");
        try { return LuaRandomState.Parse(reader.GetString()!); }
        catch (ArgumentException error) { throw new JsonException(error.Message, error); }
    }
    public override void Write(Utf8JsonWriter writer, LuaRandomState value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
