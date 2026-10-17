namespace NovellMaker.Editor.ViewModels;

public sealed record ExplorerEntry(string Label, string Detail, IReadOnlyList<ExplorerEntry> Children, Guid? SceneId = null, Guid? ImageAssetId = null, bool IsAssetLibrary = false)
{
    public ExplorerEntry(string label, string detail = "") : this(label, detail, []) { }
}
