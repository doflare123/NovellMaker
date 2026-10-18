namespace NovellMaker.Editor.ViewModels;

public sealed class CharacterLibraryDocumentViewModel : DocumentViewModel
{
    public EditorSession Session { get; }
    public override string Header => Session.CharacterLibraryTitle;
    public override bool CanClose => true;
    public override AsyncCommand CloseCommand { get; }

    internal CharacterLibraryDocumentViewModel(EditorSession session)
    {
        Session = session;
        CloseCommand = EditorSession.Command(() => Session.CloseCharacterLibrary(), () => Session.IsIdle);
    }
}
