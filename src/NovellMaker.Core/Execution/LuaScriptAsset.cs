using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Execution;

/// <summary>A project-owned Lua source resource. Identity is independent of its display name.</summary>
public sealed record LuaScriptAsset(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Source)
{
    [JsonRequired]
    public ImmutableDictionary<string, Guid> References { get; init; } = ImmutableDictionary<string, Guid>.Empty;

    public static LuaScriptAsset Create(string name) => new(Guid.NewGuid(), name, "-- game.set('flag', 'true')\n");

    public bool ContentEquals(LuaScriptAsset other) => Id == other.Id && Name == other.Name && Source == other.Source &&
        References.Count == other.References.Count && References.All(r => other.References.TryGetValue(r.Key, out var id) && id == r.Value);

    internal void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Source is null ||
            Source.Length > LuaActionScript.MaximumSourceLength || References is null || !LuaActionScript.ValidReferences(References))
            throw new ArgumentException("Некорректный ресурс Lua: ID, имя, код или именованные ссылки.");
    }
}
