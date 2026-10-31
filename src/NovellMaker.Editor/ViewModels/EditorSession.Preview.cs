using NovellMaker.Editor.Services.Preview;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand OpenExampleCommand { get; private set; } = null!;
    public AsyncCommand PreviewSceneCommand { get; private set; } = null!;

    private void InitializePreview()
    {
        PreviewSceneCommand = new(PreviewSceneAsync, () => CanEdit && ActiveScene is not null && IsNameValid && AreDocumentsValid);
        OpenExampleCommand = new(OpenExampleAsync, () => !IsBusy);
    }

    public Task OpenExampleAsync() => RunAsync(async () =>
    {
        if (!await MayLeaveProjectAsync()) return;
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker", "Examples", Guid.NewGuid().ToString("N"));
        var example = Services.BuiltInExample.Create(directory);
        var path = Path.Combine(directory, "example.novell");
        string? exampleRevision = null;
        if (store is NovellMaker.Core.Projects.JsonProjectStore jsonStore)
            exampleRevision = await jsonStore.SaveIfUnchangedAsync(path, example, null);
        else await store.SaveAsync(path, example);
        SetProject(example, path, saved: true);
        fileRevision = exampleRevision;
        InitializeScriptFiles();
        await AwaitScriptValidationAsync();
        OpenScene(example.Scenes[0].Id);
        ActiveScene!.SelectedSection = 4;
        Log("Открыта отдельная копия примера во временной папке. F6 — запуск; F7/F8 в игре — сохранить/загрузить. Используйте «Сохранить как…» для постоянной копии.");
    });

    public Task PreviewSceneAsync() => RunAsync(async () =>
    {
        if (project is null) return;
        _ = project.ReleaseCheck?.ToConfiguration();
        var document = ActiveScene;
        var sceneId = document?.Id;
        if (sceneId is null) return;
        Services.DialogueDisplayValidation.Validate(project);
        await ValidateExternalScriptsForRunAsync();
        if (!IsNameValid || !AreDocumentsValid || document is { IsEditingLanguageValid: false })
            throw new InvalidOperationException("Исправьте названия и язык перед запуском предпросмотра.");
        var request = new PreviewRequest(project, ProjectDirectory ?? Path.GetTempPath(), sceneId.Value,
            project.Scenes.First(s => s.Id == sceneId).HasActionScript ? null : document?.SelectedLine?.Id, document?.EditingLanguage ?? project.DefaultLanguage);
        await previewService.StartAsync(request, Log);
        Log("Запущен игровой предпросмотр снимка сцены. Повторный запуск применит текущие изменения.");
    });

    public void Dispose() { sessionDisposed = true; DisposeRecovery(); StopScriptWatcher(); previewService.Dispose(); }
}
