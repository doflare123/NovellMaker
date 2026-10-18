using NovellMaker.Editor.Services.Preview;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand PreviewSceneCommand { get; private set; } = null!;

    private void InitializePreview() => PreviewSceneCommand = new(PreviewSceneAsync, () => CanEdit && ActiveScene is not null && IsNameValid && AreDocumentsValid);

    public Task PreviewSceneAsync() => RunAsync(async () =>
    {
        if (project is null || ActiveScene is not { } document) return;
        if (!IsNameValid || !AreDocumentsValid || !document.IsEditingLanguageValid)
            throw new InvalidOperationException("Исправьте названия и язык перед запуском предпросмотра.");
        var request = new PreviewRequest(project, ProjectDirectory ?? Path.GetTempPath(), document.Id, document.SelectedLine?.Id, document.EditingLanguage);
        await previewService.StartAsync(request, Log);
        Log("Запущен игровой предпросмотр снимка сцены. Повторный запуск применит текущие изменения.");
    });

    public void Dispose() => previewService.Dispose();
}
