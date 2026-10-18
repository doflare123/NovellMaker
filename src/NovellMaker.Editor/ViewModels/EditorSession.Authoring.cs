using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private readonly List<AuthoringSnapshot> undo = [];
    private readonly List<AuthoringSnapshot> redo = [];
    private readonly Dictionary<Guid, SceneDocumentViewModel> sceneDocuments = [];
    private string? editGroup;
    private DocumentViewModel? selectedDocument;
    private string projectCssError = "";
    private DialogueStyle projectStyle = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    public AsyncCommand AddSceneCommand { get; private set; } = null!;
    public AsyncCommand DeleteSceneCommand { get; private set; } = null!;
    public AsyncCommand UndoCommand { get; private set; } = null!;
    public AsyncCommand RedoCommand { get; private set; } = null!;
    public bool CanUndo => !IsBusy && undo.Count > 0;
    public bool CanRedo => !IsBusy && redo.Count > 0;
    public bool AreDocumentsValid => project is null ||
        (!string.IsNullOrWhiteSpace(project.DefaultLanguage) && project.Scenes.All(scene => !string.IsNullOrWhiteSpace(scene.Name)) &&
         project.Images.All(image => !string.IsNullOrWhiteSpace(image.Name)) &&
         project.Characters.All(character => !string.IsNullOrWhiteSpace(character.Name) && character.States.All(state => !string.IsNullOrWhiteSpace(state.Name))));
    public string DocumentValidation => AreDocumentsValid ? "" : "Укажите основной язык и названия сцен, изображений, персонажей и состояний.";
    public SceneDocumentViewModel? ActiveScene => selectedDocument as SceneDocumentViewModel;
    public string SelectedSceneId => ActiveScene?.Id.ToString() ?? "—";
    public string SelectedLineId => ActiveScene?.SelectedLine?.Id.ToString() ?? "—";
    public string SelectedTextKey => ActiveScene?.SelectedLine?.TextKey ?? "—";

    public DocumentViewModel? SelectedDocument
    {
        get => selectedDocument;
        set
        {
            if (ReferenceEquals(selectedDocument, value)) return;
            selectedDocument = value;
            EndEditGroup();
            NotifySelectionChanged();
        }
    }

    public string DefaultLanguage
    {
        get => project?.DefaultLanguage ?? "ru";
        set { if (project is not null) EditProject(project with { DefaultLanguage = value ?? "" }, "default-language"); }
    }

    public string DialogueCss
    {
        get => project?.DialogueUi.Css ?? DialogueCssParser.DefaultCss;
        set
        {
            if (project is not null) EditProject(project with { DialogueUi = project.DialogueUi with { Css = value ?? "" } }, "project-css");
        }
    }

    public bool ShowVoiceProgress
    {
        get => project?.DialogueUi.ShowVoiceProgress ?? true;
        set { if (project is not null) EditProject(project with { DialogueUi = project.DialogueUi with { ShowVoiceProgress = value } }); }
    }

    public bool ShowVoiceControls
    {
        get => project?.DialogueUi.ShowVoiceControls ?? true;
        set { if (project is not null) EditProject(project with { DialogueUi = project.DialogueUi with { ShowVoiceControls = value } }); }
    }

    public bool ShowNavigationControls
    {
        get => project?.DialogueUi.ShowNavigationControls ?? true;
        set { if (project is not null) EditProject(project with { DialogueUi = project.DialogueUi with { ShowNavigationControls = value } }); }
    }

    public string ProjectCssError => projectCssError;
    public DialogueStyle? ProjectStyle => projectStyle;

    private void InitializeAuthoring()
    {
        AddSceneCommand = Command(AddScene, () => CanEdit);
        DeleteSceneCommand = new(() => ActiveScene is { } active ? DeleteSceneAsync(active.Id) : Task.CompletedTask, () => CanEdit && ActiveScene is not null);
        UndoCommand = new(() => RunHistoryAsync(Undo), () => CanUndo);
        RedoCommand = new(() => RunHistoryAsync(Redo), () => CanRedo);
    }

    internal static AsyncCommand Command(Action action, Func<bool> enabled) => new(() => { action(); return Task.CompletedTask; }, enabled);

    public void AddScene()
    {
        if (!CanEdit || project is null) return;
        var number = 1;
        while (project.Scenes.Any(scene => scene.Name == $"Сцена {number}")) number++;
        var scene = NovelScene.Create($"Сцена {number}");
        EditProject(project with { Scenes = project.Scenes.Add(scene) });
        OpenScene(scene.Id);
    }

    public void OpenScene(Guid id)
    {
        var scene = project?.Scenes.FirstOrDefault(item => item.Id == id);
        if (scene is null) return;
        if (!sceneDocuments.TryGetValue(id, out var document))
        {
            document = new SceneDocumentViewModel(this, scene);
            sceneDocuments.Add(id, document);
        }
        if (!Documents.Contains(document)) Documents.Add(document);
        SelectedDocument = document;
    }

    public void CloseDocument(SceneDocumentViewModel document)
    {
        if (IsBusy) return;
        Documents.Remove(document);
        sceneDocuments.Remove(document.Id);
        if (ReferenceEquals(SelectedDocument, document)) SelectedDocument = Documents.FirstOrDefault();
    }

    public void DeleteActiveScene()
    {
        if (!CanEdit || project is null || ActiveScene is not { } active) return;
        var scene = project.Scenes.FirstOrDefault(item => item.Id == active.Id);
        if (scene is not null) EditProject(project with { Scenes = project.Scenes.Remove(scene) });
    }

    internal void UpdateScene(NovelScene scene, string? group = null)
    {
        if (project is null) return;
        var index = -1;
        for (var position = 0; position < project.Scenes.Length; position++)
            if (project.Scenes[position].Id == scene.Id) { index = position; break; }
        if (index >= 0) EditProject(project with { Scenes = project.Scenes.SetItem(index, scene) }, group);
    }

    internal NovelScene? SavedScene(Guid id) => savedProject?.Scenes.FirstOrDefault(scene => scene.Id == id);

    internal void NotifySelectionChanged()
    {
        foreach (var property in new[] { nameof(SelectedDocument), nameof(ActiveScene), nameof(SelectedSceneId), nameof(SelectedLineId), nameof(SelectedTextKey) })
            Changed(property);
        NotifyAuthoringCommands();
    }

    private void EditProject(NovelProject updated, string? group = null)
    {
        if (project is null || project.ContentEquals(updated)) return;
        RecordEdit(group);
        project = updated;
        NotifyProjectChanged();
    }

    public void EndEditGroup() => editGroup = null;

    private AuthoringSnapshot Snapshot() => new(project!, draftName, ActiveScene?.Id, ActiveScene?.SelectedLine?.Id,
        SelectedDocument is AssetLibraryDocumentViewModel, SelectedImage?.Id,
        SelectedDocument is CharacterLibraryDocumentViewModel, SelectedCharacter?.Id, SelectedState?.Id, deletedFiles);

    private void RecordEdit(string? group)
    {
        if (project is null) return;
        if (group is null || editGroup != group)
        {
            undo.Add(Snapshot());
            if (undo.Count > 200) undo.RemoveAt(0);
        }
        redo.Clear();
        editGroup = group;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var current = Snapshot();
        var previous = undo[^1];
        Restore(previous);
        undo.RemoveAt(undo.Count - 1);
        redo.Add(current);
        NotifyAuthoringCommands();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var current = Snapshot();
        var next = redo[^1];
        Restore(next);
        redo.RemoveAt(redo.Count - 1);
        undo.Add(current);
        NotifyAuthoringCommands();
    }

    private void Restore(AuthoringSnapshot state)
    {
        RestoreDeletedFiles(state.DeletedFiles);
        project = state.Project;
        draftName = state.DraftName;
        EndEditGroup();
        NotifyProjectChanged();
        if (state.SceneId is { } sceneId)
        {
            OpenScene(sceneId);
            ActiveScene?.SelectLine(state.LineId);
        }
        else if (state.AssetLibrarySelected)
        {
            OpenAssetLibrary();
            SelectedImage = Images.FirstOrDefault(image => image.Id == state.ImageId);
        }
        else if (state.CharacterLibrarySelected)
        {
            OpenCharacterLibrary();
            SelectedCharacter = Characters.FirstOrDefault(character => character.Id == state.CharacterId);
            SelectedState = SelectedCharacter?.States.FirstOrDefault(pose => pose.Id == state.CharacterStateId);
        }
        else SelectedDocument = Documents.FirstOrDefault();
    }

    private void ResetAuthoring()
    {
        undo.Clear();
        redo.Clear();
        deletedFiles = [];
        EndEditGroup();
        sceneDocuments.Clear();
        projectStyle = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
        projectCssError = "";
        Documents.Clear();
        if (project is not null) Documents.Add(new ProjectDocumentViewModel(this));
        SelectedDocument = Documents.FirstOrDefault();
    }

    private void NotifyAuthoringChanged()
    {
        foreach (var document in sceneDocuments.Values.ToArray())
        {
            var scene = project?.Scenes.FirstOrDefault(item => item.Id == document.Id);
            if (scene is not null) document.Synchronize(scene);
            else
            {
                Documents.Remove(document);
                sceneDocuments.Remove(document.Id);
            }
        }
        if (SelectedDocument is null || !Documents.Contains(SelectedDocument)) SelectedDocument = Documents.FirstOrDefault();
        foreach (var document in Documents) document.RefreshHeader();
        try { projectStyle = DialogueCssParser.Parse(DialogueCss); projectCssError = ""; }
        catch (DialogueCssException error) { projectCssError = error.Message; }
        foreach (var property in new[] { nameof(DefaultLanguage), nameof(DialogueCss), nameof(ShowVoiceProgress), nameof(ShowVoiceControls),
                     nameof(ShowNavigationControls), nameof(ProjectCssError), nameof(ProjectStyle), nameof(AreDocumentsValid), nameof(DocumentValidation) })
            Changed(property);
        NotifySelectionChanged();
    }

    private void NotifyAuthoringCommands()
    {
        Changed(nameof(CanUndo));
        Changed(nameof(CanRedo));
        foreach (var command in new[] { AddSceneCommand, DeleteSceneCommand, UndoCommand, RedoCommand }) command?.NotifyCanExecuteChanged();
        PreviewSceneCommand?.NotifyCanExecuteChanged();
        foreach (var document in sceneDocuments.Values) document.NotifyCommands();
    }

    private sealed record AuthoringSnapshot(NovelProject Project, string DraftName, Guid? SceneId, Guid? LineId,
        bool AssetLibrarySelected, Guid? ImageId, bool CharacterLibrarySelected, Guid? CharacterId, Guid? CharacterStateId, ImmutableArray<NovellMaker.Editor.Services.ProjectFileDeletion> DeletedFiles);
}
