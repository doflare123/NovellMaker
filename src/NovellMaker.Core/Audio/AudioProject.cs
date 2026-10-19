using System.Collections.Immutable;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Audio;

public enum AudioGroup { Master, Sound, Effect, Voice }
public sealed record AudioAsset(Guid Id, string Name, string RelativePath);
public sealed record AudioBus(float Volume = 1, bool Muted = false);
public sealed record AudioProject
{
    [System.Text.Json.Serialization.JsonRequired]
    public ImmutableArray<AudioAsset> Assets { get; init; } = [];
    [System.Text.Json.Serialization.JsonRequired]
    public ImmutableDictionary<Guid, Guid> SceneSounds { get; init; } = ImmutableDictionary<Guid, Guid>.Empty;
    [System.Text.Json.Serialization.JsonRequired]
    public ImmutableDictionary<Guid, Guid> LineEffects { get; init; } = ImmutableDictionary<Guid, Guid>.Empty;
    [System.Text.Json.Serialization.JsonRequired]
    public ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>> Voices { get; init; } = ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>>.Empty;
    [System.Text.Json.Serialization.JsonRequired]
    public ImmutableDictionary<AudioGroup, AudioBus> Mixer { get; init; } = ImmutableDictionary<AudioGroup, AudioBus>.Empty;

    public static void Validate(NovelProject project)
    {
        var audio = project.Audio ?? throw new ArgumentException("Нет настроек звука.");
        if (audio.Assets.IsDefault || audio.SceneSounds is null || audio.LineEffects is null || audio.Voices is null || audio.Mixer is null)
            throw new ArgumentException("Коллекции звука не инициализированы.");
        var ids = project.Images.Select(a => a.Id).Concat(project.Characters.Select(c => c.Id))
            .Concat(project.Characters.SelectMany(c => c.States).Select(s => s.Id))
            .Concat(project.Scenes.Select(s => s.Id)).Concat(project.Scenes.SelectMany(s => s.Lines).Select(l => l.Id)).Append(project.Id).ToHashSet();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in audio.Assets)
        {
            if (asset is null || asset.Id == Guid.Empty || !ids.Add(asset.Id) || string.IsNullOrWhiteSpace(asset.Name))
                throw new ArgumentException("Некорректный звуковой ресурс или повторный ID.");
            AudioAssetPath.ValidateRelativePath(asset.RelativePath);
            if (!paths.Add(asset.RelativePath)) throw new ArgumentException("Повторный путь звукового ресурса.");
        }
        bool Asset(Guid id) => audio.Assets.Any(a => a.Id == id);
        var lines = project.Scenes.SelectMany(s => s.Lines).Select(l => l.Id).ToHashSet();
        if (audio.SceneSounds.Any(p => !project.Scenes.Any(s => s.Id == p.Key) || !Asset(p.Value)) ||
            audio.LineEffects.Any(p => !lines.Contains(p.Key) || !Asset(p.Value)) ||
            audio.Voices.Any(p => !lines.Contains(p.Key) || p.Value is null || p.Value.Any(v => string.IsNullOrWhiteSpace(v.Key) || !Asset(v.Value))) ||
            audio.Mixer.Any(p => !Enum.IsDefined(p.Key) || p.Value is null || !float.IsFinite(p.Value.Volume) || p.Value.Volume is < 0 or > 1))
            throw new ArgumentException("Некорректная привязка звука или громкость.");
    }

    public AudioProject Prune(NovelProject project)
    {
        var lines = project.Scenes.SelectMany(s => s.Lines).Select(l => l.Id).ToHashSet();
        return this with {
            SceneSounds = SceneSounds.Where(p => Assets.Any(a => a.Id == p.Value) && project.Scenes.Any(s => s.Id == p.Key)).ToImmutableDictionary(),
            LineEffects = LineEffects.Where(p => Assets.Any(a => a.Id == p.Value) && lines.Contains(p.Key)).ToImmutableDictionary(),
            Voices = Voices.Where(p => lines.Contains(p.Key)).ToImmutableDictionary(p => p.Key, p => p.Value.Where(v => Assets.Any(a => a.Id == v.Value)).ToImmutableDictionary())
        };
    }
}
