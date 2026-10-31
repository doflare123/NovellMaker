using System.Collections.Immutable;

namespace NovellMaker.Core.Profiles;

/// <summary>Player-owned data, independent of gameplay sessions and project revisions.</summary>
public sealed record PlayerProfileData(PlayerSettings Settings, ImmutableDictionary<string, string> Data)
{
    public const int MaximumReadLines = 10000;
    public ImmutableArray<string> ReadLines { get; init; } = [];

    public const int MaximumEntries = 256;
    public const int MaximumValueCharacters = 1024;
    public const int MaximumCharacters = 16384;

    public void Validate()
    {
        if (Settings is null || Data is null || Data.Count > MaximumEntries ||
            Data.Any(p => !AuthorKey.IsValid(p.Key) || p.Value is null || p.Value.Length > MaximumValueCharacters) ||
            Data.Sum(p => p.Key.Length + p.Value.Length) > MaximumCharacters)
            throw new ArgumentException("Некорректные постоянные данные или превышен бюджет профиля.");
        if (ReadLines.IsDefault || ReadLines.Length > MaximumReadLines ||
            ReadLines.Any(k => k is null || k.Length != 64 || !k.All(char.IsAsciiHexDigit)) || ReadLines.Distinct().Count() != ReadLines.Length)
            throw new ArgumentException("Некорректные отметки прочитанных реплик.");
        Settings.Validate();
    }
}

public sealed record PersistentDataChange(string Key, string? Value)
{
    public void Validate()
    {
        if (!AuthorKey.IsValid(Key) || Value is { Length: > PlayerProfileData.MaximumValueCharacters })
            throw new ArgumentException("Некорректный ключ или значение постоянных данных.");
    }
}
