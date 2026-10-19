using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Projects;

/// <summary>Versioned entry point for a distributable game; all paths are relative to the executable.</summary>
public sealed record GamePackage(int Version, Guid ProjectId, Guid StartSceneId, string Language, string Title)
{
    public const string ManifestName = "game.json";
    public const string ProjectName = "game.novell";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };

    public static GamePackage Load(string directory)
    {
        var package = JsonSerializer.Deserialize<GamePackage>(File.ReadAllText(Path.Combine(directory, ManifestName)), Options)
            ?? throw new InvalidDataException("Пустой манифест игры.");
        if (package.Version != 1 || package.ProjectId == Guid.Empty || package.StartSceneId == Guid.Empty ||
            string.IsNullOrWhiteSpace(package.Language) || string.IsNullOrWhiteSpace(package.Title))
            throw new InvalidDataException("Некорректный или неподдерживаемый манифест игры.");
        return package;
    }

    public Task SaveAsync(string directory) => File.WriteAllTextAsync(Path.Combine(directory, ManifestName),
        JsonSerializer.Serialize(this, Options));
}
