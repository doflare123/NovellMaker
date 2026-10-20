using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Preview;

namespace NovellMaker.Editor.ViewModels;

/// <summary>Owns one authoring project. No rendering or game execution lives here.</summary>
public sealed partial class EditorSession : INotifyPropertyChanged, IDisposable
{
    private readonly IProjectStore store;
    private readonly IEditorDialogs dialogs;
    private readonly IGitStatusService git;
    private readonly IImageAssetService imageAssets;
    private readonly IPreviewService previewService;
    private NovelProject? project;
    private NovelProject? savedProject;
    private string draftName = "";
    private string? projectPath;
    private bool isBusy;
    private string status = "Создайте или откройте проект.";
    private string gitSummary = "Git: проект не открыт";

    public EditorSession(IProjectStore store, IEditorDialogs dialogs, IGitStatusService git, IImageAssetService? imageAssets = null, IPreviewService? previewService = null, IExternalCodeEditor? codeEditor = null, RecentProjectsStore? recentProjects = null)
    {
        this.store = store;
        this.dialogs = dialogs;
        this.git = git;
        this.imageAssets = imageAssets ?? new ImageAssetService();
        this.previewService = previewService ?? new PreviewService();
        this.codeEditor = codeEditor ?? new ExternalCodeEditor();
        recentProjectsStore = recentProjects ?? new RecentProjectsStore(dialogs is EditorDialogs ? RecentProjectsStore.ProfilePath : null);
        foreach (var recent in recentProjectsStore.Read()) RecentProjects.Add(recent);
        NewCommand = new(NewProjectAsync, () => !IsBusy);
        OpenCommand = new(OpenProjectAsync, () => !IsBusy);
        SaveCommand = new(() => SaveAsync(), () => !IsBusy && HasProject && IsNameValid && AreDocumentsValid);
        SaveAsCommand = new(() => SaveAsync(saveAs: true), () => !IsBusy && HasProject && IsNameValid && AreDocumentsValid);
        CloseProjectCommand = new(() => CloseProjectAsync(), () => !IsBusy && HasProject);
        RefreshGitCommand = new(RefreshGitAsync, () => !IsBusy && ProjectPath is not null);
        InitializeAuthoring();
        InitializeScripts();
        InitializeAssets();
        InitializeCharacters();
        InitializePreview();
        InitializeExport();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ExplorerEntry> Explorer { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public AsyncCommand NewCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SaveAsCommand { get; }
    public AsyncCommand CloseProjectCommand { get; }
    public AsyncCommand RefreshGitCommand { get; }
    public NovelProject? Project => project;
    public string? ProjectPath => projectPath;
    public bool HasProject => project is not null;
    public bool IsDirty => project is not null && (!project.ContentEquals(savedProject) || draftName != savedProject?.Name);
    public bool IsNameValid => !string.IsNullOrWhiteSpace(ProjectName);
    public string NameValidation => IsNameValid || !HasProject ? "" : "Введите название проекта.";
    public bool CanEdit => HasProject && !IsBusy;
    public string ProjectId => project?.Id.ToString() ?? "—";
    public string FormatVersion => project?.FormatVersion.ToString() ?? "—";
    public string PathDisplay => projectPath ?? "Проект ещё не сохранён";
    public string DocumentTitle => "Параметры проекта" + (IsDirty ? " *" : "");
    public string WindowTitle => HasProject ? $"{ProjectName}{(IsDirty ? " *" : "")} — NovellMaker" : "NovellMaker";
    public string Status => status;
    public string GitSummary => gitSummary;
    public bool IsBusy => isBusy;
    public bool IsIdle => !isBusy;

    public string ProjectName
    {
        get => draftName;
        set
        {
            value ??= "";
            if (project is null || draftName == value) return;
            RecordEdit("project-name");
            draftName = value;
            // Incomplete text is a draft, not a valid Core model.
            if (!string.IsNullOrWhiteSpace(value)) project = project with { Name = value };
            NotifyProjectChanged();
        }
    }

    public Task NewProjectAsync() => RunAsync(async () =>
    {
        if (!await MayLeaveProjectAsync()) return;
        SetProject(NovelProject.Create("Новая новелла"), null, saved: false);
        Log("Создан новый проект. Укажите название и сохраните его.");
    });

    public Task OpenProjectAsync() => RunAsync(async () =>
    {
        var path = await dialogs.PickOpenProjectAsync();
        if (path is null) return;
        await OpenProjectCoreAsync(path);
    });

    private async Task OpenProjectCoreAsync(string path)
    {
        // Validate the candidate before asking to discard a working project.
        await store.LoadAsync(path);
        if (!await MayLeaveProjectAsync()) return;
        // Saving from the confirmation may have changed this very file.
        var loaded = await store.LoadAsync(path);
        SetProject(loaded, Path.GetFullPath(path), saved: true);
        InitializeScriptFiles();
        RememberProject();
        Log("Проект открыт.");
        await UpdateGitAsync();
    }

    public Task SaveAsync(bool saveAs = false) => RunAsync(async () => { await SaveCoreAsync(saveAs); });

    public async Task<bool> CloseProjectAsync()
    {
        var closed = false;
        await RunAsync(async () =>
        {
            if (!await MayLeaveProjectAsync()) return;
            SetProject(null, null, saved: false);
            Log("Проект закрыт.");
            closed = true;
        });
        return closed;
    }

    public async Task<bool> RequestCloseAsync()
    {
        var allowed = false;
        await RunAsync(async () => { allowed = await MayLeaveProjectAsync(); });
        return allowed;
    }

    public Task RefreshGitAsync() => RunAsync(async () =>
    {
        RefreshExternalScriptsCore();
        RefreshAssetFiles();
        await UpdateGitAsync();
    });

    private async Task<bool> MayLeaveProjectAsync()
    {
        if (!IsDirty) return true;
        return (await dialogs.ConfirmUnsavedChangesAsync(ProjectName)) switch
        {
            UnsavedChangesDecision.Discard => true,
            UnsavedChangesDecision.Save => await SaveCoreAsync(false) && !IsDirty,
            _ => false
        };
    }

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        if (project is null) return false;
        if (!IsNameValid || !AreDocumentsValid)
        {
            await dialogs.ShowErrorAsync("Не удалось сохранить проект", !IsNameValid ? "Введите название проекта." : DocumentValidation);
            return false;
        }
        var path = saveAs ? null : projectPath;
        path ??= await dialogs.PickSaveProjectAsync("project.novell");
        if (path is null) return false;
        path = Path.GetFullPath(path);
        RefreshExternalScriptsCore();
        var snapshot = project!;
        if (savedProject is not null && projectPath is not null && string.Equals(path, projectPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            // Catch edits by another editor instance before replacing an existing project.
            var onDisk = await store.LoadAsync(path);
            if (!onDisk.ContentEquals(savedProject))
                throw new IOException("Файл проекта изменён вне редактора. Откройте его заново или используйте «Сохранить как…» для сохранения своей версии отдельно.");
        }
        if (ProjectDirectory is { } sourceRoot && snapshot.Images.Length > 0 &&
            !string.Equals(sourceRoot, Path.GetDirectoryName(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            await imageAssets.CopyAsync(sourceRoot, Path.GetDirectoryName(path)!, snapshot.Images);
        if (ProjectDirectory is { } audioRoot && snapshot.Audio.Assets.Length > 0 &&
            !string.Equals(audioRoot, Path.GetDirectoryName(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            await Services.Assets.AudioAssetService.CopyAsync(audioRoot, Path.GetDirectoryName(path)!, snapshot.Audio.Assets);
        var targetRoot = Path.GetDirectoryName(path)!;
        var copyingScripts = ProjectDirectory is null || !string.Equals(ProjectDirectory, targetRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (copyingScripts)
            foreach (var script in snapshot.Scripts) ScriptFiles.Ensure(targetRoot, script, requireSame: true);
        await store.SaveAsync(path, snapshot);
        if ((deletedFiles.Length > 0 || undo.Any(s => s.DeletedFiles.Length > 0) || redo.Any(s => s.DeletedFiles.Length > 0)) && projectPath is not null && !string.Equals(Path.GetDirectoryName(path), ProjectDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            // File undo belongs to the old filesystem root, not the newly copied project.
            undo.Clear(); redo.Clear(); deletedFiles = [];
        }
        savedProject = snapshot;
        projectPath = path;
        StartScriptWatcher();
        EndEditGroup();
        NotifyProjectChanged();
        Log("Проект сохранён.");
        RememberProject();
        await UpdateGitAsync();
        return true;
    }

    private void SetProject(NovelProject? value, string? path, bool saved)
    {
        project = value;
        draftName = value?.Name ?? "";
        savedProject = saved ? value : null;
        projectPath = path;
        ResetAuthoring();
        ResetAssets();
        ResetCharacters();
        SelectedScriptId = null;
        InspectorTarget = InspectorTarget.Project; InspectedActionId = null;
        StopScriptWatcher();
        scriptDiagnostics.Clear();
        gitSummary = path is null ? "Git: сохраните проект для проверки" : "Git: проверка…";
        if (value is null) gitSummary = "Git: проект не открыт";
        fileExplorerRoot = null;
        fileGitSnapshot = null;
        Explorer.Clear();
        NotifyProjectChanged();
    }

    private void NotifyProjectChanged()
    {
        foreach (var name in new[] { nameof(Project), nameof(ProjectName), nameof(HasProject), nameof(IsDirty),
                     nameof(IsNameValid), nameof(NameValidation), nameof(CanEdit), nameof(ProjectId), nameof(FormatVersion),
                     nameof(ExplorerModeIndex), nameof(IsFolderExplorer), nameof(ProjectPath), nameof(PathDisplay), nameof(DocumentTitle), nameof(WindowTitle), nameof(GitSummary) })
            Changed(name);
        NotifyAssetsChanged();
        NotifyCharactersChanged();
        Changed(nameof(ScriptLibraryTitle));
        Changed(nameof(SelectedScript));
        RebuildProjectEntry();
        NotifyAuthoringChanged();
        NotifyCommands();
    }

    private void RebuildProjectEntry()
    {
        if (project is null) return;
        if (project.ExplorerShowsFiles)
        {
            RebuildFileExplorer();
            return;
        }
        var row = new ExplorerEntry(IsNameValid ? ProjectName : "Без названия", "Проект новеллы",
            [new ExplorerEntry(projectPath is null ? "Параметры проекта *" : Path.GetFileName(projectPath), PathDisplay, [], IsProjectSettings: true),
             new ExplorerEntry($"Сцены ({project.Scenes.Length})", "Сцены проекта",
                 project.Scenes.Select(scene => new ExplorerEntry(string.IsNullOrWhiteSpace(scene.Name) ? "Сцена без названия" : scene.Name,
                     scene.Id.ToString(), [], scene.Id)).ToArray()),
             new ExplorerEntry($"Изображения ({project.Images.Length})", "Библиотека изображений",
                 project.Images.Select(image => new ExplorerEntry(image.Name, image.RelativePath, [], ImageAssetId: image.Id)).ToArray(), IsAssetLibrary: true),
             new ExplorerEntry($"Персонажи ({project.Characters.Length})", "Персонажи и состояния",
                 project.Characters.Select(character => new ExplorerEntry(character.Name, character.Id.ToString(), [], CharacterId: character.Id)).ToArray(), IsCharacterLibrary: true),
             new ExplorerEntry($"Lua-скрипты ({project.Scripts.Length})", "Lua-ресурсы внутри .novell",
                 project.Scripts.Select(script => new ExplorerEntry(script.Name, script.Id.ToString(), [], ScriptId: script.Id)).ToArray(), IsScriptLibrary: true)]);
        if (Explorer.Count == 0) Explorer.Add(row);
        else Explorer[0] = row;
    }

    private async Task UpdateGitAsync()
    {
        if (projectPath is null) return;
        var result = await git.GetStatusAsync(Path.GetDirectoryName(projectPath)!);
        fileGitSnapshot = result;
        fileExplorerRoot = null;
        RebuildProjectEntry();
        gitSummary = result.Message;
        Changed(nameof(GitSummary));
        var entries = result.Changes.Select(change => new ExplorerEntry(
            $"{change.IndexStatus}{change.WorkTreeStatus}  {change.Path}",
            change.OriginalPath is null ? change.DisplayStatus : $"{change.DisplayStatus}: {change.OriginalPath} → {change.Path}")).ToArray();
        var gitEntry = new ExplorerEntry("Изменения Git", result.Message, entries);
        if (Explorer.Count > 1) Explorer[1] = gitEntry;
        else Explorer.Add(gitEntry);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (isBusy) return;
        isBusy = true;
        BusyChanged();
        try { await operation(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Ошибка: {ex.Message}");
            await dialogs.ShowErrorAsync("Операция не выполнена", ex.Message);
        }
        finally
        {
            isBusy = false;
            BusyChanged();
        }
    }

    private void BusyChanged()
    {
        Changed(nameof(IsBusy));
        Changed(nameof(IsIdle));
        Changed(nameof(CanEdit));
        NotifyAuthoringCommands();
        NotifyAssetCommands();
        NotifyCharacterCommands();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        foreach (var command in new[] { NewCommand, OpenCommand, SaveCommand, SaveAsCommand, CloseProjectCommand, RefreshGitCommand, OpenExampleCommand, ExportWindowsCommand, ExportWindowsInstallerCommand, OpenScriptLibraryCommand })
            command.NotifyCanExecuteChanged();
    }

    private void Log(string message)
    {
        status = message;
        Changed(nameof(Status));
        Diagnostics.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        if (Diagnostics.Count > 100) Diagnostics.RemoveAt(Diagnostics.Count - 1);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
