using NovellMaker.Editor.Services.Preview;
using NovellMaker.Core.Execution;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

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

    public ObservableCollection<string> PreviewJournal { get; } = [];
    public ImmutableDictionary<string, string> PreviewVariables { get; private set; } = ImmutableDictionary<string, string>.Empty;
    internal NovellMaker.Core.Minigames.MinigameOutcome? PreviewOutcome { get; private set; }
    private long debugGeneration;
    private void ResetPreviewDebug()
    {
        debugGeneration++;
        PreviewOutcome = null;
        PreviewJournal.Clear();
        PreviewVariables = ImmutableDictionary<string, string>.Empty;
        Changed(nameof(PreviewVariables));
    }
    public Task PreviewSceneAsync() => PreviewSceneAsync(null);

    internal Task PreviewSceneAsync(PreviewStart? start) => RunAsync(async () =>
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
        var scene = project.Scenes.First(s => s.Id == sceneId);
        var line = document?.SelectedLine?.Id;
        if (start is null && !scene.ScriptBlocks.IsEmpty && line.HasValue && scene.Lines[0].Id != line)
            start = new(ScenarioProgram.LineEntryId(line.Value), ImmutableDictionary<string, string>.Empty);
        ResetPreviewDebug();
        var generation = debugGeneration;
        var request = new PreviewRequest(project, ProjectDirectory ?? Path.GetTempPath(), sceneId.Value,
            scene.HasActionScript || start is not null ? null : line, document?.EditingLanguage ?? project.DefaultLanguage)
        {
            Start = start,
            Trace = entry =>
            {
                if (generation != debugGeneration) return;
                if (entry.Snapshot is not null)
                {
                    PreviewVariables = entry.Snapshot;
                    PreviewOutcome = entry.Outcome;
                }
                if (entry.Variable is not null && entry.Value is not null)
                    PreviewVariables = PreviewVariables.SetItem(entry.Variable, entry.Value);
                if (entry.Snapshot is not null || entry.Variable is not null) Changed(nameof(PreviewVariables));
                if (PreviewJournal.Count >= 2000) PreviewJournal.RemoveAt(0);
                PreviewJournal.Add(entry.Message);
            }
        };
        await previewService.StartAsync(request, Log);
        Log("Запущен игровой предпросмотр снимка сцены. Повторный запуск применит текущие изменения.");
    });

    public void Dispose() { sessionDisposed = true; DisposeRecovery(); StopScriptWatcher(); previewService.Dispose(); }
}
