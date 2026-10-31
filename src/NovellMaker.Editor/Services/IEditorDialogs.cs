namespace NovellMaker.Editor.Services;

public enum UnsavedChangesDecision { Save, Discard, Cancel }
public sealed record WindowsExportDestination(string Directory, Guid StartSceneId);
public sealed record WindowsInstallerDestination(string FilePath, Guid StartSceneId, Export.WindowsInstallerOptions Options);

public interface IEditorDialogs
{
    Task<string?> PickOpenProjectAsync();
    Task<string?> PickSaveProjectAsync(string suggestedName);
    Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName);
    Task<RecoveryDecision> ConfirmRecoveryAsync(RecoveryOffer offer) => Task.FromResult(RecoveryDecision.Later);
    Task<ExternalProjectDecision> ConfirmExternalProjectChangeAsync(string path, bool hasUnsavedChanges, bool canReload) => Task.FromResult(ExternalProjectDecision.Keep);
    Task<bool> ConfirmDeleteAsync(string name, string consequences) => Task.FromResult(false);
    Task ShowErrorAsync(string title, string message);
    Task ShowInformationAsync(string title, string message) => Task.CompletedTask;
    Task<string?> PickAudioAsync() => Task.FromResult<string?>(null);
    Task<string?> PickLuaScriptAsync() => Task.FromResult<string?>(null);
    Task<string?> PickCodeEditorAsync() => Task.FromResult<string?>(null);
    Task<string?> EditLuaNameAsync(string suggestedName) => Task.FromResult<string?>(suggestedName);
    Task<Guid?> PickScriptAsync(IReadOnlyList<NovellMaker.Core.Execution.LuaScriptAsset> scripts) => Task.FromResult<Guid?>(scripts.FirstOrDefault()?.Id);
    Task<string?> PickSaveLuaScriptAsync(string suggestedName) => Task.FromResult<string?>(null);
    Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
    Task<WindowsExportDestination?> PickWindowsExportAsync(NovellMaker.Core.Projects.NovelProject project) => Task.FromResult<WindowsExportDestination?>(null);
    Task<WindowsInstallerDestination?> PickWindowsInstallerAsync(NovellMaker.Core.Projects.NovelProject project) => Task.FromResult<WindowsInstallerDestination?>(null);
}
