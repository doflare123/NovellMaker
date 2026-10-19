namespace NovellMaker.Editor.Services;

public enum UnsavedChangesDecision { Save, Discard, Cancel }
public sealed record WindowsExportDestination(string Directory, Guid StartSceneId);
public sealed record WindowsInstallerDestination(string FilePath, Guid StartSceneId, Export.WindowsInstallerOptions Options);

public interface IEditorDialogs
{
    Task<string?> PickOpenProjectAsync();
    Task<string?> PickSaveProjectAsync(string suggestedName);
    Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName);
    Task<bool> ConfirmDeleteAsync(string name, string consequences) => Task.FromResult(false);
    Task ShowErrorAsync(string title, string message);
    Task ShowInformationAsync(string title, string message) => Task.CompletedTask;
    Task<string?> PickAudioAsync() => Task.FromResult<string?>(null);
    Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
    Task<WindowsExportDestination?> PickWindowsExportAsync(NovellMaker.Core.Projects.NovelProject project) => Task.FromResult<WindowsExportDestination?>(null);
    Task<WindowsInstallerDestination?> PickWindowsInstallerAsync(NovellMaker.Core.Projects.NovelProject project) => Task.FromResult<WindowsInstallerDestination?>(null);
}
