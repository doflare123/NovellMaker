using NovellMaker.Core.Audio;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private AudioMixSettings Playback => project?.Audio.Playback ?? new();
    public double AudioFadeInSeconds { get => Playback.FadeInSeconds; set => EditPlayback(Playback with { FadeInSeconds = value }); }
    public double AudioFadeOutSeconds { get => Playback.FadeOutSeconds; set => EditPlayback(Playback with { FadeOutSeconds = value }); }
    public double AudioCrossfadeSeconds { get => Playback.CrossfadeSeconds; set => EditPlayback(Playback with { CrossfadeSeconds = value }); }
    public bool AudioDuckingEnabled { get => Playback.DuckingEnabled; set => EditPlayback(Playback with { DuckingEnabled = value }); }
    public double AudioDuckingVolume { get => Playback.DuckingVolume; set => EditPlayback(Playback with { DuckingVolume = (float)value }); }
    public double AudioDuckingAttackSeconds { get => Playback.DuckingAttackSeconds; set => EditPlayback(Playback with { DuckingAttackSeconds = value }); }
    public double AudioDuckingReleaseSeconds { get => Playback.DuckingReleaseSeconds; set => EditPlayback(Playback with { DuckingReleaseSeconds = value }); }
    private void EditPlayback(AudioMixSettings value)
    {
        value.Validate();
        if (project is not null) UpdateAudio(project.Audio with { Playback = value });
    }
    private void NotifyAudioPlayback()
    {
        foreach (var name in new[] { nameof(AudioFadeInSeconds), nameof(AudioFadeOutSeconds), nameof(AudioCrossfadeSeconds),
            nameof(AudioDuckingEnabled), nameof(AudioDuckingVolume), nameof(AudioDuckingAttackSeconds), nameof(AudioDuckingReleaseSeconds) }) Changed(name);
    }
    internal bool IsAudioModified => project is not null && (savedProject is null ? project.Audio.Assets.Length > 0 : !AudioContent.Equals(project.Audio, savedProject.Audio));
    public Task ImportAudioAsync() => RunAsync(async () =>
    {
        if (project is null) return;
        var source = await dialogs.PickAudioAsync();
        if (source is null) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var asset = await AudioAssetService.ImportAsync(ProjectDirectory!, source);
        EditProject(project with { Audio = project.Audio with { Assets = project.Audio.Assets.Add(asset) } });
        Log($"Звук «{asset.Name}» импортирован.");
        await UpdateGitAsync();
    });
    internal void UpdateAudio(AudioProject audio, string? group = null)
    {
        if (CanEdit && project is not null) EditProject(project with { Audio = audio }, group);
    }
}
