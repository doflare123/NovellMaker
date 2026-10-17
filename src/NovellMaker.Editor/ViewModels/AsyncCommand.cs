using System.Windows.Input;

namespace NovellMaker.Editor.ViewModels;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();

    // All operations report failures through EditorSession and IEditorDialogs.
    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            await execute();
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
