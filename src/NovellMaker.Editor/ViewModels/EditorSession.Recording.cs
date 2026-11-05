using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NovellMaker.Core.Audio;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private readonly Dictionary<string, (long Length, DateTime Modified, string Description)> voiceDescriptions = new();
    internal string DescribeVoice(AudioAsset asset)
    {
        if (ProjectDirectory is null) return asset.Name;
        try
        {
            var path = AudioAssetPath.Resolve(ProjectDirectory, asset.RelativePath); var info = new FileInfo(path);
            if (voiceDescriptions.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc) return cached.Description;
            using var input = File.OpenRead(path); var wave = PcmWave.Inspect(input);
            var description = $"{asset.Name} · {wave.Length / (double)(wave.SampleRate * wave.Channels * 2):0.0} с";
            voiceDescriptions[path] = (info.Length, info.LastWriteTimeUtc, description);
            return description;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return asset.Name + " · файл недоступен"; }
    }
    public Task AttachLineVoiceAsync(Guid sceneId, Guid lineId, string language, string? source = null) => RunAsync(async () =>
    {
        if (project is null || string.IsNullOrWhiteSpace(language) || !project.Scenes.Any(s => s.Id == sceneId && s.Lines.Any(l => l.Id == lineId))) return;
        source ??= await dialogs.PickAudioAsync();
        if (source is null) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var asset = await AudioAssetService.ImportAsync(ProjectDirectory!, source);
        var voices = project.Audio.Voices.GetValueOrDefault(lineId) ?? ImmutableDictionary<string, Guid>.Empty;
        EditProject(project with { Audio = project.Audio with { Assets = project.Audio.Assets.Add(asset), Voices = project.Audio.Voices.SetItem(lineId, voices.SetItem(language, asset.Id)),
            VoiceOmissions = SceneDocumentViewModel.ClearOmission(project.Audio, lineId, language) } });
        Log($"Озвучка «{asset.Name}» прикреплена ({language})."); await UpdateGitAsync();
    });
    public Task ExportRecordingSheetAsync(Guid sceneId, string language) => RunAsync(async () =>
    {
        if (project is null || string.IsNullOrWhiteSpace(language)) return;
        var scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId); if (scene is null) return;
        var path = await dialogs.PickSaveRecordingSheetAsync($"voice-{scene.Id:N}-{language}.tsv"); if (path is null) return;
        await File.WriteAllTextAsync(path, RecordingSheet.Write(RecordingSheet.Rows(project, scene, language), Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t'), new UTF8Encoding(true));
        Log("Лист записи экспортирован: " + path);
    });
    public Task ImportRecordingSheetAsync(Guid sceneId) => RunAsync(async () =>
    {
        if (project is null || !project.Scenes.Any(s => s.Id == sceneId)) return;
        var sheet = await dialogs.PickRecordingSheetAsync(); if (sheet is null) return;
        var rows = RecordingSheet.Read(await File.ReadAllTextAsync(sheet));
        var folder = await dialogs.PickVoiceFolderAsync(); if (folder is null) return;
        var plan = await RecordingSheet.PreviewAsync(project, sceneId, rows, folder);
        if (!await dialogs.ConfirmVoiceImportAsync(plan) || plan.Matches == 0) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        await ApplyVoiceImportCoreAsync(plan);
    });
    public Task ApplyVoiceImportAsync(VoiceImportPlan plan) => RunAsync(async () =>
    {
        if (project is null || project.Id != plan.ProjectId) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        await ApplyVoiceImportCoreAsync(plan);
    });
    private async Task ApplyVoiceImportCoreAsync(VoiceImportPlan plan)
    {
        if (project is null || project.Id != plan.ProjectId || !project.Scenes.Any(s => s.Id == plan.SceneId)) throw new InvalidOperationException("Проект листа записи изменился.");
        var imported = new List<AudioAsset>(); var audio = project.Audio;
        try
        {
            foreach (var item in plan.Items.Where(i => i.CanImport))
            {
                var row = item.Row;
                var line = project.Scenes.Single(s => s.Id == plan.SceneId).Lines.FirstOrDefault(l => l.Id == row.LineId);
                if (line is null || !line.Texts.ContainsKey(row.Language) || audio.VoiceOmissions.GetValueOrDefault(row.LineId)?.Contains(row.Language) == true ||
                    (audio.Voices.GetValueOrDefault(row.LineId)?.TryGetValue(row.Language, out var current) == true ? current : (Guid?)null) != item.PreviousAssetId)
                    throw new InvalidOperationException("Привязки изменились. Повторите предпросмотр импорта.");
                var asset = await AudioAssetService.ImportAsync(ProjectDirectory!, item.Source!); imported.Add(asset);
                using (var input = File.OpenRead(AudioAssetPath.Resolve(ProjectDirectory!, asset.RelativePath)))
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input)) != item.Hash) throw new IOException("WAV изменён после предпросмотра: " + row.Wav);
                var voices = audio.Voices.GetValueOrDefault(row.LineId) ?? ImmutableDictionary<string, Guid>.Empty;
                audio = audio with { Assets = audio.Assets.Add(asset), Voices = audio.Voices.SetItem(row.LineId, voices.SetItem(row.Language, asset.Id)),
                    VoiceOmissions = SceneDocumentViewModel.ClearOmission(audio, row.LineId, row.Language) };
            }
            if (imported.Count == 0) return;
            // One immutable snapshot: a failed copy never changes bindings, Undo restores the complete batch.
            EditProject(project with { Audio = audio });
        }
        catch
        {
            foreach (var asset in imported) File.Delete(AudioAssetPath.Resolve(ProjectDirectory!, asset.RelativePath));
            throw;
        }
        Log($"Импортировано записей: {imported.Count}."); await UpdateGitAsync();
    }
}
