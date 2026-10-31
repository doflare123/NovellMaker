using Avalonia.Threading;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private readonly EditorRecoveryStore recoveryStore;
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private EditorRecoveryStore.Lease? recoveryLease;
    private EditorRecoverySnapshot? lastRecovery;
    private DispatcherTimer? recoveryTimer;
    private string? fileRevision;
    private string? observedExternalRevision;
    private bool requiresSaveAs;
    private bool preserveRecoveredScripts;
    private string recoveryStatus = "";
    private bool maintenancePending;
    public string RecoveryStatus => recoveryStatus;

    // The UI thread captures immutable model data; only serialization/disk I/O run in the background.
    internal async Task AutosaveAsync()
    {
        if (!recoveryStore.Enabled || sessionDisposed) return;
        await recoveryGate.WaitAsync();
        try
        {
            if (sessionDisposed) return;
            if (!IsDirty)
            {
                DeleteRecoveryCore();
                return;
            }
            var snapshot = new EditorRecoverySnapshot(1, project!, savedProject, draftName, projectPath, fileRevision,
                DateTimeOffset.UtcNow, ActiveScene?.Id, ActiveScene?.SelectedLine?.Id);
            if (lastRecovery is { } previous && snapshot.Project.ContentEquals(previous.Project) &&
                snapshot.DraftName == previous.DraftName && snapshot.ProjectPath == previous.ProjectPath &&
                snapshot.FileRevision == previous.FileRevision && NovelProject.ContentEquals(snapshot.SavedProject, previous.SavedProject)) return;
            recoveryLease ??= recoveryStore.Create();
            await recoveryLease.WriteAsync(snapshot);
            lastRecovery = snapshot;
            SetRecoveryStatus($"Снимок восстановления: {snapshot.CreatedAt.ToLocalTime():HH:mm:ss}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            SetRecoveryStatus("Ошибка автосохранения: " + error.Message);
        }
        finally { recoveryGate.Release(); }
    }

    private void SetRecoveryStatus(string value)
    {
        if (recoveryStatus == value) return;
        recoveryStatus = value;
        Changed(nameof(RecoveryStatus));
        if (value.StartsWith("Ошибка", StringComparison.Ordinal)) Log(value);
    }

    private async Task DeleteRecoveryAsync()
    {
        await recoveryGate.WaitAsync();
        try { DeleteRecoveryCore(); }
        finally { recoveryGate.Release(); }
    }

    private void DeleteRecoveryCore()
    {
        recoveryLease?.Delete();
        recoveryLease?.Dispose();
        recoveryLease = null;
        lastRecovery = null;
        SetRecoveryStatus("");
    }

    public Task RecoverOnStartupAsync() => RunAsync(async () =>
    {
        if (HasProject || !recoveryStore.Enabled) return;
        foreach (var path in recoveryStore.Candidates())
        {
            var lease = recoveryStore.Claim(path);
            if (lease is null) continue;
            try
            {
                EditorRecoverySnapshot snapshot;
                try { snapshot = await lease.ReadAsync(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
                {
                    Log($"Не удалось прочитать снимок восстановления {Path.GetFileName(path)}: {error.Message}");
                    continue; // Retain damaged/unsupported files for inspection; offer other valid snapshots.
                }
                var changed = snapshot.ProjectPath is not null && snapshot.FileRevision is not null &&
                    !await RevisionMatchesAsync(snapshot.ProjectPath, snapshot.FileRevision);
                var decision = await dialogs.ConfirmRecoveryAsync(new(snapshot.DraftName, snapshot.ProjectPath, snapshot.CreatedAt, changed));
                if (decision == RecoveryDecision.Discard) { lease.Delete(); continue; }
                if (decision != RecoveryDecision.Restore) continue;
                SetProject(snapshot.Project, snapshot.ProjectPath, saved: false);
                savedProject = snapshot.SavedProject;
                draftName = snapshot.DraftName;
                fileRevision = snapshot.FileRevision;
                requiresSaveAs = snapshot.ProjectPath is not null && snapshot.SavedProject is null;
                preserveRecoveredScripts = true;
                recoveryLease = lease;
                lease = null;
                lastRecovery = snapshot;
                // Do not import working Lua files here: that would silently replace recovered source text.
                NotifyProjectChanged();
                if (snapshot.SceneId is { } sceneId) { OpenScene(sceneId); ActiveScene?.SelectLine(snapshot.LineId); }
                SetRecoveryStatus($"Восстановлен снимок от {snapshot.CreatedAt.ToLocalTime():g}");
                Log(changed ? "Работа восстановлена. Основной файл изменён; сохраните свою версию через «Сохранить как…»." :
                    "Несохранённая работа восстановлена. Основной проект не изменён.");
                return;
            }
            finally { lease?.Dispose(); }
        }
    });

    private static async Task<bool> RevisionMatchesAsync(string path, string revision)
    {
        try { return await ProjectFileRevision.ReadAsync(path) == revision; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal void StartRecoveryMaintenance()
    {
        if (recoveryTimer is not null) return;
        recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        recoveryTimer.Tick += async (_, _) =>
        {
            if (maintenancePending || IsBusy || sessionDisposed) return;
            maintenancePending = true;
            try { await AutosaveAsync(); await CheckExternalProjectAsync(); }
            finally { maintenancePending = false; }
        };
        recoveryTimer.Start();
    }

    private async Task<(NovelProject Project, string? Revision)> LoadTrackedProjectAsync(string path)
    {
        // Pair the loaded model with its actual bytes, including formatting and pre-migration data.
        var before = store is JsonProjectStore ? await ProjectFileRevision.ReadAsync(path) : null;
        var loaded = await store.LoadAsync(path);
        if (before is not null && !await RevisionMatchesAsync(path, before))
            throw new IOException("Файл проекта изменился во время чтения. Повторите открытие.");
        return (loaded, before);
    }

    public Task CheckExternalProjectAsync() => RunAsync(CheckExternalProjectCoreAsync);

    private async Task CheckExternalProjectCoreAsync()
    {
        if (projectPath is null || savedProject is null || store is not JsonProjectStore) return;
        string revision;
        try { revision = await ProjectFileRevision.ReadAsync(projectPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { revision = "unreadable"; }
        if (revision == fileRevision) { observedExternalRevision = null; return; }
        if (revision == observedExternalRevision) return;
        NovelProject? onDisk = null;
        try
        {
            onDisk = await store.LoadAsync(projectPath);
            if (!await RevisionMatchesAsync(projectPath, revision)) return; // Retry a partial external write on the next tick.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProjectFormatException) { }
        observedExternalRevision = revision;
        await AutosaveAsync();
        var decision = await dialogs.ConfirmExternalProjectChangeAsync(projectPath, IsDirty, onDisk is not null);
        if (decision == ExternalProjectDecision.SaveAs) { await SaveCoreAsync(true); return; }
        if (decision != ExternalProjectDecision.Reload || onDisk is null)
        {
            Log("Файл проекта изменён вне редактора. Текущая версия оставлена; используйте «Сохранить как…».");
            return;
        }
        var path = projectPath;
        var loaded = await LoadTrackedProjectAsync(path);
        await DeleteRecoveryAsync();
        SetProject(loaded.Project, path, saved: true);
        fileRevision = loaded.Revision;
        InitializeScriptFiles();
        await AwaitScriptValidationAsync();
        Log("Загружена внешняя версия проекта.");
    }

    private void DisposeRecovery()
    {
        recoveryTimer?.Stop();
        // Dispose is also used for abrupt session shutdown: never erase an unsaved snapshot here.
        // An in-flight writer must retain ownership until its atomic commit finishes.
        if (recoveryGate.Wait(0))
        {
            try { recoveryLease?.Dispose(); recoveryLease = null; }
            finally { recoveryGate.Release(); }
        }
        else _ = ReleaseRecoveryAfterWriteAsync();
    }

    private async Task ReleaseRecoveryAfterWriteAsync()
    {
        await recoveryGate.WaitAsync();
        try { recoveryLease?.Dispose(); recoveryLease = null; }
        finally { recoveryGate.Release(); }
    }
}
