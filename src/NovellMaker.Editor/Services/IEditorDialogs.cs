namespace NovellMaker.Editor.Services;

public enum UnsavedChangesDecision { Save, Discard, Cancel }
public sealed record WindowsExportDestination(string Directory, Guid StartSceneId);

public interface IEditorDialogs
{
    Task<string?> PickOpenProjectAsync();
    Task<string?> PickSaveProjectAsync(string suggestedName);
    Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName);
    Task<bool> ConfirmDeleteAsync(string name, string consequences) => Task.FromResult(false);
    Task ShowErrorAsync(string title, string message);
    Task ShowInformationAsync(string title, string message) => Task.CompletedTask;
    Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
    Task<WindowsExportDestination?> PickWindowsExportAsync(NovellMaker.Core.Projects.NovelProject project) => Task.FromResult<WindowsExportDestination?>(null);
}
