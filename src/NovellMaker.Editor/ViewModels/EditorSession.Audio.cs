using NovellMaker.Core.Audio;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
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
