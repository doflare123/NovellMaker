using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Projects;

/// <summary>Versioned entry point for a distributable game; all paths are relative to the executable.</summary>
public sealed record GamePackage(int Version, Guid ProjectId, Guid StartSceneId, string Language, string Title)
{
    public Guid? StartScreenSceneId { get; init; }
    public int SaveContainerVersion { get; init; } = GameSlotStore.CurrentVersion;
    public const string ManifestName = "game.json";
    public const string ProjectName = "game.novell";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    private static readonly JsonSerializerOptions LegacyOptions = CreateLegacyOptions();
    private static JsonSerializerOptions CreateLegacyOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(GamePackage))
                foreach (var property in info.Properties.Where(p => p.Name is nameof(StartScreenSceneId) or nameof(SaveContainerVersion)).ToArray()) info.Properties.Remove(property);
        });
        return new(Options) { TypeInfoResolver = resolver };
    }

    public static GamePackage Load(string directory)
    {
        var text = File.ReadAllText(Path.Combine(directory, ManifestName));
        using var document = JsonDocument.Parse(text);
        var version = document.RootElement.GetProperty("Version").GetInt32();
        if (version == 2 && (!document.RootElement.TryGetProperty(nameof(StartScreenSceneId), out _) || !document.RootElement.TryGetProperty(nameof(SaveContainerVersion), out _)))
            throw new InvalidDataException("В манифесте v2 отсутствуют параметры стартового экрана/слотов.");
        var package = JsonSerializer.Deserialize<GamePackage>(text, version == 1 ? LegacyOptions : Options)
            ?? throw new InvalidDataException("Пустой манифест игры.");
        if (package.Version is not (1 or 2) || package.ProjectId == Guid.Empty || package.StartSceneId == Guid.Empty ||
            package.Version == 2 && (package.StartScreenSceneId == Guid.Empty || package.SaveContainerVersion != GameSlotStore.CurrentVersion) ||
            string.IsNullOrWhiteSpace(package.Language) || string.IsNullOrWhiteSpace(package.Title))
            throw new InvalidDataException("Некорректный или неподдерживаемый манифест игры.");
        return package;
    }

    public void ValidateProject(NovelProject project)
    {
        if (project.Id != ProjectId || !project.Scenes.Any(s => s.Id == StartSceneId) ||
            Version == 2 && StartScreenSceneId != project.StartScreenSceneId)
            throw new InvalidDataException("Манифест и проект содержат разные параметры запуска.");
    }

    public Task SaveAsync(string directory) => File.WriteAllTextAsync(Path.Combine(directory, ManifestName),
        JsonSerializer.Serialize(this, Version == 1 ? LegacyOptions : Options));
}
