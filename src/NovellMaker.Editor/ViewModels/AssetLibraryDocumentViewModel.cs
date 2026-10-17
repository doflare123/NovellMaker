namespace NovellMaker.Editor.ViewModels;

public sealed class AssetLibraryDocumentViewModel : DocumentViewModel
{
    public EditorSession Session { get; }
    public override string Header => Session.AssetLibraryTitle;
    public override bool CanClose => true;
    public override AsyncCommand CloseCommand { get; }

    internal AssetLibraryDocumentViewModel(EditorSession session)
    {
        Session = session;
        CloseCommand = EditorSession.Command(() => Session.CloseAssetLibrary(), () => Session.IsIdle);
    }
}
