namespace NovellMaker.Editor.Services;

public enum UnsavedChangesDecision { Save, Discard, Cancel }

public interface IEditorDialogs
{
    Task<string?> PickOpenProjectAsync();
    Task<string?> PickSaveProjectAsync(string suggestedName);
    Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName);
    Task ShowErrorAsync(string title, string message);
    Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
}
