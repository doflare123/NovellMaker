using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovellMaker.Editor.ViewModels;

public abstract class DocumentViewModel : INotifyPropertyChanged
{
    public abstract string Header { get; }
    public abstract bool CanClose { get; }
    public abstract AsyncCommand? CloseCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void RefreshHeader() => Changed(nameof(Header));
}

public sealed class ProjectDocumentViewModel(EditorSession session) : DocumentViewModel
{
    public EditorSession Session { get; } = session;
    public override string Header => Session.DocumentTitle;
    public override bool CanClose => false;
    public override AsyncCommand? CloseCommand => null;
}
