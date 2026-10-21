using System.ComponentModel;

namespace NovellMaker.Editor.ViewModels;

public sealed record ExplorerEntry(string Label, string Detail, IReadOnlyList<ExplorerEntry> Children, Guid? SceneId = null, Guid? ImageAssetId = null, bool IsAssetLibrary = false,
    Guid? CharacterId = null, bool IsCharacterLibrary = false, string? FilePath = null, bool IsProjectSettings = false,
    bool ExpandInitially = true, Func<IReadOnlyList<ExplorerEntry>>? ChildLoader = null, bool IsDirectory = false, string GitStatus = "", Guid? CharacterStateId = null,
    Guid? ScriptId = null, bool IsScriptLibrary = false, Guid? ScreenId = null, bool IsScreenLibrary = false) : INotifyPropertyChanged
{
    public bool IsLua => ScriptId is not null || FilePath is { } file && Path.GetExtension(file).Equals(".lua", StringComparison.OrdinalIgnoreCase);
    public bool IsScene => SceneId is not null;
    public string IconKind => IsLua ? "lua" : IsScene ? "scene" : IsFolder ? "folder" : "file";
    public string FolderName => Label.TrimEnd('/');
    public IEnumerable<ExplorerEntry> Folders => Items.Where(entry => entry.IsFolder);
    private bool isExpanded = ExpandInitially;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsExpanded
    {
        get => isExpanded;
        set { if (isExpanded == value) return; isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
    }
    public bool IsFolder => IsDirectory || ChildLoader is not null || Children.Count > 0;
    private IReadOnlyList<ExplorerEntry>? loadedChildren;
    public IReadOnlyList<ExplorerEntry> Items => loadedChildren ??= ChildLoader?.Invoke() ?? Children;

    public ExplorerEntry(string label, string detail = "") : this(label, detail, []) { }
}
