using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Core.Minigames;

/// <summary>A bounded value schema. Object fields, when specified, are exact and required.</summary>
public sealed record MinigameSchema
{
    [JsonRequired] public LuaValueKind Kind { get; init; } = LuaValueKind.Object;
    [JsonRequired] public ImmutableDictionary<string, LuaValueKind> Fields { get; init; } = ImmutableDictionary<string, LuaValueKind>.Empty;

    public void ValidateDefinition()
    {
        if (!Enum.IsDefined(Kind) || Fields is null || Fields.Count > 64 ||
            Kind != LuaValueKind.Object && Fields.Count != 0 ||
            Fields.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || !Enum.IsDefined(p.Value)))
            throw new ArgumentException("Некорректная схема данных мини-игры.");
    }
    public void Validate(LuaValue value)
    {
        ValidateDefinition();
        if (value is null || value.Kind != Kind || Fields.Count != 0 &&
            (value.Properties!.Count != Fields.Count || Fields.Any(p => !value.Properties.TryGetValue(p.Key, out var field) || field.Kind != p.Value)))
            throw new ArgumentException("Данные мини-игры не соответствуют схеме.");
    }
}

/// <summary>Optional behavior of an ordinary author scene; its ID is the resource ID.</summary>
public sealed record MinigameDefinition
{
    [JsonRequired] public MinigameSchema ParametersSchema { get; init; } = new();
    [JsonRequired] public MinigameSchema StateSchema { get; init; } = new();
    [JsonRequired] public MinigameSchema ResultSchema { get; init; } = new();
    [JsonRequired] public LuaValue DefaultParameters { get; init; } = LuaValue.FromObject([]);
    [JsonRequired] public LuaValue InitialState { get; init; } = LuaValue.FromObject([]);
    [JsonRequired] public bool CanSave { get; init; } = true;
    [JsonRequired] public string SaveDisabledReason { get; init; } = "Автор запретил сохранение этой мини-игры.";
    [JsonRequired] public double UpdatesPerSecond { get; init; } = 5;

    public void Validate()
    {
        if (ParametersSchema is null || StateSchema is null || ResultSchema is null ||
            !double.IsFinite(UpdatesPerSecond) || UpdatesPerSecond is < 1 or > 20 ||
            string.IsNullOrWhiteSpace(SaveDisabledReason) || SaveDisabledReason.Length > 256)
            throw new ArgumentException("Некорректный ресурс мини-игры.");
        ParametersSchema.Validate(DefaultParameters); StateSchema.Validate(InitialState); ResultSchema.ValidateDefinition();
    }
}
