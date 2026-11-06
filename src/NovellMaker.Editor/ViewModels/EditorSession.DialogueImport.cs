using System.Collections.Immutable;
using System.Security.Cryptography;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Audio;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand ImportDialogueCommand { get; private set; } = null!;
    public Task ImportDialogueAsync(Guid? sceneId = null, Guid? lineId = null, string? language = null) => RunAsync(async () =>
    {
        if (project is null) return;
        var plan = await dialogs.ConfigureDialogueImportAsync(project, sceneId ?? ActiveScene?.Id, lineId ?? ActiveScene?.SelectedLine?.Id, language ?? ActiveScene?.EditingLanguage ?? project.DefaultLanguage, ProjectDirectory);
        if (plan is null || !plan.CanApply) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        await ApplyDialogueImportCoreAsync(plan);
    });
    public Task ApplyDialogueImportAsync(DialogueImportPlan plan) => RunAsync(async () =>
    {
        if (project is null || !plan.CanApply) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        await ApplyDialogueImportCoreAsync(plan);
    });
    private async Task ApplyDialogueImportCoreAsync(DialogueImportPlan plan)
    {
        if (project is null || !project.ContentEquals(plan.Original)) throw new InvalidOperationException("Проект изменён после предпросмотра. Повторите импорт.");
        var files = new List<string>(); var updated = plan.Draft;
        try
        {
            foreach (var resource in plan.Resources)
            {
                // Re-resolve the approved relative path as well as checking the copied bytes.
                var source = DialogueImportPlanner.ResolveSource(plan.Options.Root, resource.RelativeSource);
                if (!resource.NeedsCopy)
                {
                    using var reusedInput = File.OpenRead(source);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(reusedInput)) != resource.Hash) throw new IOException("Ресурс изменён после предпросмотра: " + resource.RelativeSource);
                    var existing = resource.IsImage ? AssetPath.Resolve(ProjectDirectory!, updated.Images.Single(i => i.Id == resource.Id).RelativePath)
                        : AudioAssetPath.Resolve(ProjectDirectory!, updated.Audio.Assets.Single(a => a.Id == resource.Id).RelativePath);
                    using var stored = File.OpenRead(existing);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(stored)) != resource.Hash) throw new IOException("Ресурс проекта изменён после предпросмотра: " + resource.RelativeSource);
                    continue;
                }
                string target;
                if (resource.IsImage)
                {
                    var image = await imageAssets.ImportAsync(ProjectDirectory!, source);
                    target = AssetPath.Resolve(ProjectDirectory!, image.RelativePath); files.Add(target);
                    var index = Array.FindIndex(updated.Images.ToArray(), i => i.Id == resource.Id);
                    updated = updated with { Images = updated.Images.SetItem(index, image with { Id = resource.Id }) };
                }
                else
                {
                    var audio = await AudioAssetService.ImportAsync(ProjectDirectory!, source);
                    target = AudioAssetPath.Resolve(ProjectDirectory!, audio.RelativePath); files.Add(target);
                    var index = Array.FindIndex(updated.Audio.Assets.ToArray(), a => a.Id == resource.Id);
                    updated = updated with { Audio = updated.Audio with { Assets = updated.Audio.Assets.SetItem(index, audio with { Id = resource.Id }) } };
                }
                using var input = File.OpenRead(target);
                if (Convert.ToHexString(await SHA256.HashDataAsync(input)) != resource.Hash) throw new IOException("Ресурс изменён после предпросмотра: " + resource.RelativeSource);
            }
            updated.Validate();
            EditProject(updated);
        }
        catch
        {
            foreach (var path in files) File.Delete(path);
            throw;
        }
        if (plan.FirstSceneId is { } scene)
        { OpenScene(scene); ActiveScene!.SelectedSection = 0; ActiveScene.SelectLine(plan.FirstLineId); }
        else if (plan.NewCharacters > 0) OpenCharacterLibrary();
        Log("Импорт применён. " + plan.Summary); await UpdateGitAsync();
    }
}
